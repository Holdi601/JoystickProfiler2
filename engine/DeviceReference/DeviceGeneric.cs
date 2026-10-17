#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // A model for a device the catalog does not know: its kind guessed from the name and what it reports, and one
    // control per button, hat direction and axis in the vocabulary of that kind (like the engine's generic layout).
    public static class DeviceGeneric
    {
        private static readonly string[] AxisTokens = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2" };

        public static RefDevice Model(DeviceProbe probe, string kind)
        {
            string name = probe == null ? string.Empty : probe.Name;
            string k = string.IsNullOrWhiteSpace(kind) ? GuessKind(probe) : kind.Trim().ToLowerInvariant();
            string hand = k == "throttle" || k == "collective" ? "left" : k == "stick" ? (DeviceIdentifier.HandFromName(name) == "left" ? "left" : "right") : "none";
            RefDevice model = new RefDevice
            {
                Id = "generic-" + DeviceVocabulary.Slug(name.Length > 0 ? name : "device"),
                Name = name.Length > 0 ? name : "Unknown device",
                Kind = k,
                Hand = hand,
                Confidence = "low",
                Origin = "generic"
            };
            if (probe != null && DeviceCatalog.NormalizeVidPid(probe.VidPid).Length > 0)
                model.VidPids.Add(DeviceCatalog.NormalizeVidPid(probe.VidPid));
            if (name.Length > 0)
                model.Names.Add(name);
            List<string> axes = probe == null || probe.Axes == null ? new List<string>() : probe.Axes.Select(a => a.ToUpperInvariant()).Distinct().ToList();
            foreach (string axis in AxisTokens.Where(axes.Contains))
                model.Axes.Add(new RefControl { Input = axis, Control = AxisControl(k, axis), Type = "analog" });
            int buttons = probe == null ? 0 : Math.Max(0, probe.Buttons);
            for (int b = 1; b <= buttons; b++)
                model.Controls.Add(new RefControl { Input = "JOY_BTN" + b.ToString(CultureInfo.InvariantCulture), Control = ButtonControl(k, b) });
            int hats = probe == null ? 0 : Math.Max(0, probe.Hats);
            string[] dirs = { "U", "R", "D", "L" };
            string[] words = { "up", "right", "down", "left" };
            for (int h = 1; h <= hats; h++)
            {
                for (int d = 0; d < 4; d++)
                    model.Controls.Add(new RefControl { Input = "JOY_BTN_POV" + h.ToString(CultureInfo.InvariantCulture) + "_" + dirs[d], Control = HatControl(k, h) + "." + words[d] });
            }
            return model;
        }

        public static string Family(string kind)
        {
            switch ((kind ?? string.Empty).ToLowerInvariant())
            {
                case "stick": case "hotas": case "base": return "stick";
                case "throttle": return "throttle";
                case "collective": return "collective";
                case "yoke": return "yoke";
                case "quadrant": return "quadrant";
                default: return "panel";
            }
        }

        public static string ButtonControl(string kind, int number)
        {
            string family = Family(kind);
            string n = number.ToString(CultureInfo.InvariantCulture);
            switch (family)
            {
                case "stick": return number == 1 ? "stick.trigger.stage1" : "stick.button.thumb." + (number - 1).ToString(CultureInfo.InvariantCulture);
                default: return family + ".button." + n;
            }
        }

        public static string HatControl(string kind, int hat)
        {
            string family = Family(kind);
            if (family == "stick" && hat == 1)
                return "stick.hat.trim";
            if (family == "panel")
                return "panel.hat." + hat.ToString(CultureInfo.InvariantCulture);
            return family + ".hat." + hat.ToString(CultureInfo.InvariantCulture);
        }

        public static string AxisControl(string kind, string axis)
        {
            int index = Array.IndexOf(AxisTokens, (axis ?? string.Empty).ToUpperInvariant());
            string n = (index + 1).ToString(CultureInfo.InvariantCulture);
            switch (Family(kind))
            {
                case "stick":
                    return index == 0 ? "stick.axis.roll" : index == 1 ? "stick.axis.pitch" : index == 5 ? "stick.axis.twist" : index >= 6 ? "stick.axis.slider" : "stick.axis.rotary." + n;
                case "throttle":
                    return index == 2 ? "throttle.axis.main" : index == 0 ? "throttle.axis.ministick.x" : index == 1 ? "throttle.axis.ministick.y" : index >= 6 ? "throttle.axis.slider." + (index - 5).ToString(CultureInfo.InvariantCulture) : "throttle.axis.rotary." + n;
                case "collective":
                    return index == 1 || index == 2 ? "collective.axis.main" : "collective.axis.rotary." + n;
                case "yoke":
                    return index == 0 ? "yoke.axis.roll" : index == 1 ? "yoke.axis.pitch" : "quadrant.axis.lever." + n;
                case "quadrant":
                    return "quadrant.axis.lever." + n;
                default:
                    if ((kind ?? string.Empty).ToLowerInvariant() == "pedals")
                        return index == 0 ? "pedals.axis.brake_left" : index == 1 ? "pedals.axis.brake_right" : "pedals.axis.rudder";
                    return "panel.axis." + n;
            }
        }

        // stick, throttle, pedals, collective, yoke, quadrant or panel from the name, else from the inputs.
        public static string GuessKind(DeviceProbe probe)
        {
            string name = probe == null ? string.Empty : (probe.ProductName + " " + probe.InstanceName).ToLowerInvariant();
            if (Regex.IsMatch(name, @"pedal|rudder|crosswind|slaw|\btpr\b|\btfrp\b"))
                return "pedals";
            if (Regex.IsMatch(name, @"collective"))
                return "collective";
            if (Regex.IsMatch(name, @"\byoke\b"))
                return "yoke";
            if (Regex.IsMatch(name, @"quadrant|\btq\b"))
                return "quadrant";
            if (Regex.IsMatch(name, @"throttle|twcs|stecs|\btqs\b"))
                return "throttle";
            if (Regex.IsMatch(name, @"stick|joystick|grip|warbrd|rhino|gunfighter|gladiator|constellation|mongoos|hotas|fssb|orion|\bab9\b"))
                return "stick";
            if (Regex.IsMatch(name, @"panel|box|button|bbi|mfd|icp|ufc|bodnar|arduino|leonardo|pico"))
                return "panel";
            if (probe != null && probe.Axes != null && probe.Axes.Count >= 2 && probe.Hats > 0)
                return "stick";
            if (probe != null && probe.Axes != null && probe.Axes.Count >= 1 && probe.Buttons >= 8)
                return "throttle";
            return "panel";
        }
    }
}
