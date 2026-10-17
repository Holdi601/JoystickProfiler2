using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Device knowledge base (DB\Devices\*.json, see docs/devices.md): for every known simulation device what each
    // DirectInput input physically is, in one shared control vocabulary ("stick.hat.tms.up", "throttle.speedbrake.aft",
    // "panel.toggle.3.up" ...). Auto Assign uses it to place functions on the right physical controls.
    internal sealed class KbControl
    {
        public string Input = string.Empty;
        public string Control = string.Empty;
        public string Label = string.Empty;
        public string Type = "momentary";
        public string Finger = string.Empty;
    }

    internal sealed class KbDevice
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Vendor = string.Empty;
        public string Kind = string.Empty;
        public string Hand = "none";
        public string Base = string.Empty;
        public string Image = string.Empty;
        public string Confidence = string.Empty;
        public List<string> VidPids = new List<string>();
        public List<string> Names = new List<string>();
        public List<KbControl> Axes = new List<KbControl>();
        public List<KbControl> Controls = new List<KbControl>();

        public bool IsGrip { get { return Base.Length > 0; } }
    }

    // A device of the user's setup: connected or picked from the catalog, with the layout JoyPro uses for it.
    internal sealed class RosterDevice
    {
        public string JoyProName = string.Empty;
        public bool Connected;
        public KbDevice Model;
        public KbDevice BaseModel;
        public bool Generic;
        public List<KbDevice> GripChoices = new List<KbDevice>();
        public string Note = string.Empty;
        // What DirectInput reports for a connected device (empty for devices added from the catalog).
        public List<int> AxisSlots = new List<int>();
        public int ButtonCount;
        public int PovCount;

        public string Kind { get { return Model != null ? Model.Kind : "other"; } }
    }

    internal static class EngineDeviceKb
    {
        public const string KbFolderVariable = "JOYPRO_DEVICE_KB";
        private static List<KbDevice> cache;

        public static string Folder()
        {
            string overrideFolder = Environment.GetEnvironmentVariable(KbFolderVariable);
            if (!string.IsNullOrWhiteSpace(overrideFolder))
                return overrideFolder;
            return Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Devices");
        }

        public static List<KbDevice> Load(List<string> warnings)
        {
            if (cache != null)
                return cache;
            List<KbDevice> devices = new List<KbDevice>();
            string folder = Folder();
            if (!Directory.Exists(folder))
            {
                warnings?.Add("Device knowledge base not found: " + folder + " (update JoyPro).");
                cache = devices;
                return devices;
            }
            // The user's own definitions (marker assistant, imported files; docs/device-reference.md) come last and replace
            // a shipped model with the same id.
            string userFolder = JoyPro.DeviceReference.DeviceCatalog.UserFolder();
            IEnumerable<string> files = Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Concat(Directory.Exists(userFolder) ? Directory.GetFiles(userFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>());
            foreach (string file in files)
            {
                try
                {
                    Dictionary<string, object> root = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    string vendor = Text(root, "vendor");
                    foreach (Dictionary<string, object> item in List(root, "devices"))
                    {
                        KbDevice device = new KbDevice
                        {
                            Id = Text(item, "id"),
                            Name = Text(item, "name"),
                            Vendor = vendor,
                            Kind = Text(item, "kind"),
                            Hand = Text(item, "hand").Length > 0 ? Text(item, "hand") : "none",
                            Base = Text(item, "base"),
                            Image = Text(item, "image"),
                            Confidence = Text(item, "confidence")
                        };
                        Dictionary<string, object> identify = item.ContainsKey("identify") ? item["identify"] as Dictionary<string, object> : null;
                        if (identify != null)
                        {
                            device.VidPids.AddRange(Strings(identify, "vidPids").Select(v => v.ToUpperInvariant()));
                            device.Names.AddRange(Strings(identify, "names"));
                        }
                        device.Axes.AddRange(List(item, "axes").Select(ParseControl));
                        device.Controls.AddRange(List(item, "controls").Select(ParseControl));
                        if (device.Id.Length == 0)
                            continue;
                        devices.RemoveAll(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
                        devices.Add(device);
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add("Device knowledge base file '" + Path.GetFileName(file) + "' could not be read: " + ex.Message);
                }
            }
            cache = devices;
            return devices;
        }

        public static KbDevice Find(string id, List<string> warnings)
        {
            return Load(warnings).FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static List<KbDevice> Search(string query, List<string> warnings)
        {
            string q = (query ?? string.Empty).Trim();
            return Load(warnings)
                .Where(d => q.Length == 0 ||
                            d.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            d.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            d.Vendor.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        // The user's setup: connected devices identified against the knowledge base (grip choices for bases that take
        // several grips), plus devices the user added from the catalog. Unknown devices get a generic layout.
        public static List<RosterDevice> BuildRoster(GameDeviceDirectory directory, IDictionary<string, string> gripChoices, IEnumerable<string> addedIds, ICollection<string> ignored, List<string> warnings)
        {
            List<KbDevice> kb = Load(warnings);
            List<RosterDevice> roster = new List<RosterDevice>();
            foreach (GameDevice device in directory.Devices.Where(d => d.Connected))
            {
                if (ignored != null && ignored.Any(i => Matches(device, i)))
                    continue;
                RosterDevice entry = new RosterDevice { JoyProName = device.JoyProName, Connected = true };
                List<KbDevice> candidates = Candidates(device, kb);
                List<KbDevice> grips = candidates.Where(c => c.IsGrip).ToList();
                foreach (string baseId in candidates.Where(c => c.Kind == "base").Select(c => c.Id))
                    grips.AddRange(kb.Where(k => string.Equals(k.Base, baseId, StringComparison.OrdinalIgnoreCase) && !grips.Contains(k)));
                // The product name names one grip exactly (VKB reports "...Modern Combat Pro Twist"): no question.
                string productName = Normalize(device.HidProductString.Length > 0 ? device.HidProductString : device.InstanceName);
                List<KbDevice> named = grips.Where(g => g.Names.Any(n => Normalize(n) == productName)).ToList();
                if (named.Count == 1)
                    grips = named;
                // A left-hand device ("L-VPC Stick", "LEFT VPC ...") only takes left-hand or two-handed grips.
                string hand = HandFromName(device.HidProductString + " " + device.InstanceName);
                if (hand.Length > 0 && grips.Any(g => g.Hand == hand))
                    grips = grips.Where(g => g.Hand == hand || g.Hand == "none" || g.Hand == "both" || g.Hand.Length == 0).ToList();
                // The stock stick of a base that also takes other grips (Warthog stick and the F/A-18C / F-16C grips on
                // its base report one USB id and name), and grips told apart by the inputs the device reports
                // (DeviceIdentifier, docs/device-reference.md).
                List<KbDevice> stock = grips.Count == 0 ? new List<KbDevice>()
                    : candidates.Where(c => !c.IsGrip && c.Kind != "base" && c.Names.Any(n => Normalize(n) == productName)).ToList();
                List<KbDevice> fitting = new List<KbDevice>();
                if (grips.Count > 1 || stock.Count == 1)
                {
                    directory.EnsureCapabilities(device, warnings);
                    fitting = BySignature(stock.Count == 1 ? stock.Concat(grips).Distinct().ToList() : grips, device, kb);
                }
                string chosen = null;
                if (gripChoices != null)
                {
                    foreach (KeyValuePair<string, string> choice in gripChoices)
                    {
                        if (Matches(device, choice.Key))
                            chosen = choice.Value;
                    }
                }

                KbDevice chosenModel = chosen == null ? null : kb.FirstOrDefault(k => string.Equals(k.Id, chosen, StringComparison.OrdinalIgnoreCase));
                if (chosenModel != null)
                {
                    entry.Model = chosenModel;
                    entry.BaseModel = chosenModel.IsGrip ? kb.FirstOrDefault(k => string.Equals(k.Id, chosenModel.Base, StringComparison.OrdinalIgnoreCase)) : null;
                }
                else if (fitting.Count == 1)
                {
                    entry.Model = fitting[0];
                    entry.BaseModel = fitting[0].IsGrip ? kb.FirstOrDefault(k => string.Equals(k.Id, fitting[0].Base, StringComparison.OrdinalIgnoreCase)) : null;
                    entry.Note = "grip told apart by the inputs it reports";
                }
                else if (stock.Count == 1)
                {
                    entry.Model = stock[0];
                    entry.GripChoices = stock.Concat(grips).Distinct().ToList();
                    entry.Note = "other grips report the same; pick yours if it is one of them (--grip)";
                }
                else if (grips.Count > 1)
                {
                    entry.GripChoices = fitting.Concat(grips).Distinct().ToList();
                    entry.Note = "several grips fit this base; pick one (--grip)";
                }
                else if (grips.Count == 1)
                {
                    entry.Model = grips[0];
                    entry.BaseModel = kb.FirstOrDefault(k => string.Equals(k.Id, grips[0].Base, StringComparison.OrdinalIgnoreCase));
                }
                else if (candidates.Count > 0)
                {
                    // Several models share a VID:PID (V2/V3 of one product): the exact product name, then confidence decides.
                    entry.Model = candidates
                        .OrderByDescending(c => Normalize(c.Names.FirstOrDefault() ?? c.Name) == productName ? 1 : 0)
                        .ThenByDescending(c => ConfidenceRank(c.Confidence))
                        .ThenByDescending(c => c.Controls.Count)
                        .ThenBy(c => c.Id, StringComparer.Ordinal).First();
                }

                if (entry.Model == null && entry.GripChoices.Count == 0)
                {
                    directory.EnsureCapabilities(device, warnings);
                    entry.Model = GenericModel(device);
                    entry.Generic = true;
                    entry.Note = "not in the device catalog; generic layout from its " + device.ButtonCount.ToString(CultureInfo.InvariantCulture) + " buttons";
                }
                if (entry.Model != null && entry.Model.Kind == "other")
                    continue;
                if (entry.Model != null)
                {
                    directory.EnsureCapabilities(device, warnings);
                    entry.AxisSlots = (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(a => a).ToList();
                    entry.ButtonCount = device.ButtonCount;
                    entry.PovCount = device.PovCount;
                }
                roster.Add(entry);
            }

            int planned = 0;
            foreach (string id in addedIds ?? Enumerable.Empty<string>())
            {
                KbDevice model = kb.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));
                if (model == null)
                {
                    warnings?.Add("Device '" + id + "' is not in the device catalog.");
                    continue;
                }
                planned++;
                roster.Add(new RosterDevice
                {
                    JoyProName = model.Name + " {00000000-0000-0000-0000-" + planned.ToString("D12", CultureInfo.InvariantCulture) + "}",
                    Connected = false,
                    Model = model,
                    BaseModel = model.IsGrip ? kb.FirstOrDefault(k => string.Equals(k.Id, model.Base, StringComparison.OrdinalIgnoreCase)) : null,
                    Note = "not connected (added from the catalog); exchange it for the real device once connected"
                });
            }
            return roster;
        }

        // The models whose inputs fit what the device reports, the closest first (all of them when the closest is not
        // unique); empty when the device's inputs are unknown or no model has known numbers.
        private static List<KbDevice> BySignature(List<KbDevice> models, GameDevice device, List<KbDevice> kb)
        {
            if (!device.CapabilitiesKnown)
                return new List<KbDevice>();
            JoyPro.DeviceReference.DeviceProbe probe = new JoyPro.DeviceReference.DeviceProbe
            {
                Buttons = device.ButtonCount,
                Hats = device.PovCount,
                Axes = (device.AxisSlots ?? new List<int>()).Where(a => a >= 0 && a <= 7).Select(GameInputTokens.AxisToken).ToList()
            };
            List<KeyValuePair<KbDevice, int>> fits = new List<KeyValuePair<KbDevice, int>>();
            foreach (KbDevice model in models)
            {
                KbDevice baseModel = model.IsGrip ? kb.FirstOrDefault(k => string.Equals(k.Id, model.Base, StringComparison.OrdinalIgnoreCase)) : null;
                List<string> inputs = model.Axes.Concat(model.Controls).Concat(baseModel == null ? Enumerable.Empty<KbControl>() : baseModel.Axes.Concat(baseModel.Controls))
                    .Select(c => c.Input).Where(i => i.Length > 0).ToList();
                if (inputs.Count == 0)
                    continue;
                int distance;
                if (JoyPro.DeviceReference.DeviceIdentifier.Fits(JoyPro.DeviceReference.DeviceSignature.Of(inputs), probe, out distance))
                    fits.Add(new KeyValuePair<KbDevice, int>(model, distance));
            }
            if (fits.Count == 0)
                return new List<KbDevice>();
            int best = fits.Min(f => f.Value);
            List<KbDevice> closest = fits.Where(f => f.Value == best).Select(f => f.Key).ToList();
            return closest.Count == 1 ? closest : fits.OrderBy(f => f.Value).Select(f => f.Key).ToList();
        }

        private static bool Matches(GameDevice device, string query)
        {
            string q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
                return false;
            if (string.Equals(device.JoyProName, q, StringComparison.OrdinalIgnoreCase))
                return true;
            if (device.HasVidPid && string.Equals(GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId), q, StringComparison.OrdinalIgnoreCase))
                return true;
            string name;
            Guid guid;
            if (GameDeviceDirectory.TryParseJoyProName(device.JoyProName, out name, out guid))
                return string.Equals(name, q, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static List<KbDevice> Candidates(GameDevice device, List<KbDevice> kb)
        {
            List<KbDevice> byId = new List<KbDevice>();
            if (device.HasVidPid)
            {
                string key = GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId).ToUpperInvariant();
                byId = kb.Where(k => k.VidPids.Contains(key)).ToList();
            }
            string name = Normalize(device.HidProductString.Length > 0 ? device.HidProductString : device.InstanceName);
            List<KbDevice> byName = kb.Where(k => k.Names.Any(n => Normalize(n) == name)).ToList();
            if (byId.Count > 0 && byName.Count > 0)
            {
                List<KbDevice> both = byId.Intersect(byName).ToList();
                if (both.Count > 0)
                    return both.Concat(byId.Where(k => k.IsGrip || k.Kind == "base")).Distinct().ToList();
            }
            if (byId.Count > 0)
                return byId;
            if (byName.Count > 0)
                return byName;
            // Last resort: the catalog name is part of the device name (e.g. "MFG Crosswind" in "MFG Crosswind V2").
            return kb.Where(k => k.Kind != "base" && k.Names.Concat(new[] { k.Name }).Select(Normalize)
                    .Any(n => n.Length >= 6 && (name.Contains(n) || n.Contains(name))))
                .OrderByDescending(k => k.Names.Concat(new[] { k.Name }).Select(Normalize).Max(n => name.Contains(n) ? n.Length : 0))
                .Take(1).ToList();
        }

        private static string HandFromName(string name)
        {
            string n = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (Regex.IsMatch(n, @"^(l-|l\s)|\bleft\b"))
                return "left";
            if (Regex.IsMatch(n, @"^(r-|r\s)|\bright\b"))
                return "right";
            return string.Empty;
        }

        private static int ConfidenceRank(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant())
            {
                case "high": return 3;
                case "medium": return 2;
                case "low": return 1;
                default: return 0;
            }
        }

        private static string Normalize(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        // Unknown device: a layout from what DirectInput reports, its kind guessed from name and capabilities.
        public static KbDevice GenericModel(GameDevice device)
        {
            string name = (device.HidProductString + " " + device.InstanceName).ToLowerInvariant().Trim();
            string kind = GuessKind(name, device);
            bool leftHand = Regex.IsMatch(name, @"^(l-|left\b|l\s)") || name.Contains(" left");
            string hand = kind == "throttle" ? "left" : kind == "stick" ? (leftHand ? "left" : "right") : "none";

            KbDevice model = new KbDevice { Id = "generic:" + device.JoyProName, Name = device.JoyProName, Kind = kind, Hand = hand, Confidence = "low" };
            string family = kind == "stick" || kind == "throttle" || kind == "collective" ? kind : kind == "pedals" ? "pedals" : "panel";
            string[] stickAxes = { "stick.axis.roll", "stick.axis.pitch", "stick.axis.slider", "stick.axis.rotary.1", "stick.axis.rotary.2", "stick.axis.twist", "stick.axis.slider", "stick.axis.rotary.3" };
            string[] throttleAxes = { "throttle.axis.ministick.x", "throttle.axis.ministick.y", "throttle.axis.main", "throttle.axis.rotary.1", "throttle.axis.rotary.2", "throttle.axis.rotary.3", "throttle.axis.slider.1", "throttle.axis.slider.2" };
            string[] pedalAxes = { "pedals.axis.brake_left", "pedals.axis.brake_right", "pedals.axis.rudder", "pedals.axis.brake_left", "pedals.axis.brake_right", "pedals.axis.rudder", "pedals.axis.rudder", "pedals.axis.rudder" };
            foreach (int axis in device.AxisSlots.Distinct().OrderBy(a => a))
            {
                if (axis < 0 || axis > 7)
                    continue;
                string control = kind == "stick" ? stickAxes[axis] : kind == "throttle" ? throttleAxes[axis] : kind == "pedals" ? pedalAxes[axis] :
                    kind == "collective" ? (axis == 1 || axis == 2 ? "collective.axis.main" : "collective.axis.rotary." + (axis + 1).ToString(CultureInfo.InvariantCulture)) :
                    "panel.axis." + (axis + 1).ToString(CultureInfo.InvariantCulture);
                model.Axes.Add(new KbControl { Input = GameInputTokens.AxisToken(axis), Control = control, Type = "analog" });
            }
            for (int b = 0; b < device.ButtonCount; b++)
            {
                string control = family == "stick" ? (b == 0 ? "stick.trigger.stage1" : "stick.button.thumb." + b.ToString(CultureInfo.InvariantCulture))
                    : family == "pedals" ? "panel.button." + (b + 1).ToString(CultureInfo.InvariantCulture)
                    : family + ".button." + (b + 1).ToString(CultureInfo.InvariantCulture);
                if (family == "throttle" && b >= 12)
                    control = "throttle.base.button." + (b + 1).ToString(CultureInfo.InvariantCulture);
                model.Controls.Add(new KbControl { Input = GameInputTokens.ButtonToken(b), Control = control });
            }
            string[] dirs = { "up", null, "right", null, "down", null, "left", null };
            for (int h = 0; h < device.PovCount; h++)
            {
                for (int d = 0; d < 8; d += 2)
                {
                    string hatFamily = family == "panel" || family == "pedals" ? "panel.button." + (200 + h * 4 + d / 2).ToString(CultureInfo.InvariantCulture)
                        : family + ".hat." + (h + 1).ToString(CultureInfo.InvariantCulture) + "." + dirs[d];
                    if (family == "stick" && h == 0)
                        hatFamily = "stick.hat.trim." + dirs[d];
                    model.Controls.Add(new KbControl { Input = GameInputTokens.PovToken(h, d), Control = hatFamily });
                }
            }
            return model;
        }

        // Name first (keyboards, wheels, pedals, throttles, sticks, panels by their usual names), capabilities last.
        private static string GuessKind(string name, GameDevice device)
        {
            if (Regex.IsMatch(name, @"wooting|keyboard|wheel|racing|vjoy|stream ?deck|moza r\d|\br\d+ base|fanatec|simucube|logitech g\d{2,3}\b|gamepad|xbox|dualsense|dualshock"))
                return "other";
            if (Regex.IsMatch(name, @"pedal|rudder|crosswind|slaw|\btpr\b|\btfrp\b"))
                return "pedals";
            if (Regex.IsMatch(name, @"collective"))
                return "collective";
            if (Regex.IsMatch(name, @"throttle|twcs|stecs|quadrant|\btqs\b|mt-50 cm(?!.*stick)|\bcm3 throttle"))
                return "throttle";
            if (Regex.IsMatch(name, @"stick|joystick|grip|warbrd|rhino|gunfighter|gladiator|constellation|mongoos|hotas|fssb|orion|cls-e|\bab9\b"))
                return "stick";
            if (Regex.IsMatch(name, @"panel|box|button|bbi|mfd|icp|ufc|takeoff|pto|combat ready|bodnar|arduino|leonardo"))
                return "panel";
            if (device.AxisSlots.Count >= 2 && device.PovCount > 0)
                return "stick";
            if (device.AxisSlots.Count >= 1 && device.ButtonCount >= 8)
                return "throttle";
            return "panel";
        }

        private static KbControl ParseControl(Dictionary<string, object> item)
        {
            return new KbControl
            {
                Input = Text(item, "input"),
                Control = Text(item, "control"),
                Label = Text(item, "label"),
                Type = Text(item, "type").Length > 0 ? Text(item, "type") : "momentary",
                Finger = Text(item, "finger")
            };
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : string.Empty;
        }

        private static IEnumerable<Dictionary<string, object>> List(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return Enumerable.Empty<Dictionary<string, object>>();
            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            return list == null ? Enumerable.Empty<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>();
        }

        private static IEnumerable<string> Strings(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return Enumerable.Empty<string>();
            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list == null || value is string)
                return new[] { Convert.ToString(value, CultureInfo.InvariantCulture) };
            return list.Cast<object>().Where(o => o != null).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture));
        }
    }
}
