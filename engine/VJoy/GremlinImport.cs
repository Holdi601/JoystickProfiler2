#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.VJoy
{
    // Joystick Gremlin profiles (.xml) into JoyPro's virtual functions and modes (docs/virtual-joystick.md "Coming from
    // Joystick Gremlin"; engine vjoy import-gremlin). Read: the devices (by GUID, else by name), the modes with their
    // inheritance and the start mode, every input's containers and actions: remaps (button, axis, hat -> vJoy), response
    // curves, keyboard mappings, macros, mode changes, merged axes, tempo / double tap / chain / smart toggle / hat
    // buttons containers and axis ranges or hat directions used as buttons. What JoyPro cannot do is listed in the
    // report, never dropped silently.
    //
    // Formats: R13 (profile version 9-13: <devices><device><mode><button|axis|hat><container><action-set>) and R14
    // (profile version 14: <inputs><input> with a root action into the <library> of actions with <property> values,
    // <modes>). Both go through one model (GBinding, GContainer, GAction) and one conversion.
    public sealed class GremlinImportResult
    {
        public string Format { get; set; }
        public List<VfMode> Modes { get; set; }
        public string StartMode { get; set; }
        public List<VirtualFunction> Functions { get; set; }
        public List<GremlinDeviceMatch> Devices { get; set; }
        // Per function: where it came from in the Gremlin profile.
        public Dictionary<string, string> Origins { get; set; }
        // Not representable in JoyPro, in plain words with the place in the profile.
        public List<string> NotImported { get; set; }
        // Imported, but approximated (what differs).
        public List<string> Notes { get; set; }
        public List<int> VJoyDevices { get; set; }

        public GremlinImportResult()
        {
            Format = StartMode = string.Empty;
            Modes = new List<VfMode>();
            Functions = new List<VirtualFunction>();
            Devices = new List<GremlinDeviceMatch>();
            Origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            NotImported = new List<string>();
            Notes = new List<string>();
            VJoyDevices = new List<int>();
        }
    }

    public sealed class GremlinDeviceMatch
    {
        public string GremlinGuid { get; set; }
        public string GremlinName { get; set; }
        public string JoyProDevice { get; set; }
        // guid | name | none
        public string Match { get; set; }
    }

    public static class GremlinImporter
    {
        private static readonly string[] AxisTokens = VfTokens.AxisTokens;
        private static readonly string[] HatDirectionNames = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        // xml: the profile; knownDevices: JoyPro device names ("Name {GUID}") to match against; vjoyDevice: when > 0,
        // the profile's vJoy devices move so its first one becomes this one (the vJoy device JoyPro tunnels into);
        // firstId: the number of the first function id ("f<n>"), used ids are skipped.
        public static GremlinImportResult Import(string xml, IList<string> knownDevices, int vjoyDevice, ICollection<string> usedIds)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(xml ?? string.Empty, LoadOptions.None);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ArgumentException("This is not a Joystick Gremlin profile (the XML cannot be read: " + ex.Message + ").");
            }
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "profile")
                throw new ArgumentException("This is not a Joystick Gremlin profile (no <profile> element).");
            Context context = new Context(knownDevices, vjoyDevice, usedIds);
            int version;
            int.TryParse(Attr(root, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
            if (root.Element("inputs") != null || root.Element("library") != null)
            {
                context.Result.Format = "Joystick Gremlin R14 (profile version " + (version > 0 ? version.ToString(CultureInfo.InvariantCulture) : "?") + ")";
                ReadR14(root, context);
            }
            else if (root.Element("devices") != null)
            {
                context.Result.Format = "Joystick Gremlin R13 (profile version " + (version > 0 ? version.ToString(CultureInfo.InvariantCulture) : "?") + ")";
                ReadR13(root, context);
            }
            else
                throw new ArgumentException("This Joystick Gremlin profile has neither <devices> (R13) nor <inputs> (R14).");
            context.Convert();
            return context.Result;
        }

        // ---------------------------------------------------------------------------------------------- model

        private sealed class GAction
        {
            public string Type = string.Empty;
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public XElement Element;
            // Container-like actions (R14 tempo, chain ...): their action sets in order.
            public List<List<GAction>> Sets = new List<List<GAction>>();

            public string Value(params string[] keys)
            {
                foreach (string key in keys)
                {
                    string value;
                    if (Values.TryGetValue(key, out value) && value != null)
                        return value.Trim();
                }
                return string.Empty;
            }
        }

        private sealed class GContainer
        {
            public string Type = "basic";
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<List<GAction>> Sets = new List<List<GAction>>();
            public bool HasConditions;
            // Axis range used as a button (Gremlin -1..1), hat directions used as a button.
            public double RangeLow = double.NaN;
            public double RangeHigh = double.NaN;
            public List<int> HatDirections = new List<int>();

            public string Value(params string[] keys)
            {
                foreach (string key in keys)
                {
                    string value;
                    if (Values.TryGetValue(key, out value) && value != null)
                        return value.Trim();
                }
                return string.Empty;
            }
        }

        private sealed class GBinding
        {
            public string DeviceGuid = string.Empty;
            public string DeviceName = string.Empty;
            public string Mode = string.Empty;
            public string InputType = string.Empty;
            public int InputId;
            public string Description = string.Empty;
            public List<GContainer> Containers = new List<GContainer>();
        }

        private sealed class GMerge
        {
            public string Mode = string.Empty;
            public string Operation = string.Empty;
            public int VJoy;
            public int Axis;
            public string LowerGuid = string.Empty;
            public string LowerName = string.Empty;
            public int LowerAxis;
            public string UpperGuid = string.Empty;
            public string UpperName = string.Empty;
            public int UpperAxis;
        }

        // ---------------------------------------------------------------------------------------------- R13

        private static void ReadR13(XElement root, Context context)
        {
            XElement settings = root.Element("settings");
            string start = settings == null ? string.Empty : Text(settings.Element("startup-mode"));
            foreach (XElement device in root.Element("devices").Elements("device"))
            {
                string guid = Attr(device, "device-guid", "guid", "id");
                string name = Attr(device, "name");
                string type = Attr(device, "type");
                if (type.Length > 0 && !string.Equals(type, "joystick", StringComparison.OrdinalIgnoreCase))
                {
                    // Keyboard (and others): modes still count, the inputs not.
                    foreach (XElement mode in device.Elements("mode"))
                    {
                        context.AddMode(Attr(mode, "name"), Attr(mode, "inherit", "parent"));
                        int inputs = mode.Elements().Count(e => e.Name.LocalName != "mode");
                        if (inputs > 0)
                            context.Result.NotImported.Add(name + " (" + type + "), mode " + Attr(mode, "name") + ": " + inputs.ToString(CultureInfo.InvariantCulture) +
                                                           " input(s) - JoyPro's virtual functions read game controllers, not the keyboard.");
                    }
                    continue;
                }
                foreach (XElement mode in device.Elements("mode"))
                {
                    string modeName = Attr(mode, "name");
                    context.AddMode(modeName, Attr(mode, "inherit", "parent"));
                    foreach (XElement input in mode.Elements())
                    {
                        string kind = input.Name.LocalName;
                        if (kind != "axis" && kind != "button" && kind != "hat")
                            continue;
                        int id;
                        if (!int.TryParse(Attr(input, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                            continue;
                        GBinding binding = new GBinding { DeviceGuid = guid, DeviceName = name, Mode = modeName, InputType = kind, InputId = id, Description = Attr(input, "description") };
                        foreach (XElement container in input.Elements("container"))
                            binding.Containers.Add(ReadR13Container(container, context));
                        if (binding.Containers.Count > 0)
                            context.Bindings.Add(binding);
                    }
                }
            }
            XElement vjoyDevices = root.Element("vjoy-devices");
            if (vjoyDevices != null)
            {
                int count = vjoyDevices.Descendants("container").Count();
                if (count > 0)
                    context.Result.NotImported.Add("vJoy devices as inputs (" + count.ToString(CultureInfo.InvariantCulture) + " container(s)): JoyPro reads physical devices only; the vJoy device is its output.");
            }
            foreach (XElement merge in root.Elements("merge-axis"))
            {
                XElement vjoy = merge.Element("vjoy");
                XElement lower = merge.Element("lower");
                XElement upper = merge.Element("upper");
                context.Merges.Add(new GMerge
                {
                    Mode = Attr(merge, "mode"),
                    Operation = Attr(merge, "operation"),
                    VJoy = Int(vjoy == null ? string.Empty : Attr(vjoy, "vjoy-id", "vjoy", "device"), 1),
                    Axis = Int(vjoy == null ? string.Empty : Attr(vjoy, "axis", "axis-id", "id"), 0),
                    LowerGuid = lower == null ? string.Empty : Attr(lower, "device-guid", "guid"),
                    LowerName = lower == null ? string.Empty : Attr(lower, "device-name", "name"),
                    LowerAxis = Int(lower == null ? string.Empty : Attr(lower, "axis", "axis-id", "id"), 0),
                    UpperGuid = upper == null ? string.Empty : Attr(upper, "device-guid", "guid"),
                    UpperName = upper == null ? string.Empty : Attr(upper, "device-name", "name"),
                    UpperAxis = Int(upper == null ? string.Empty : Attr(upper, "axis", "axis-id", "id"), 0)
                });
            }
            if (root.Element("plugins") != null && root.Element("plugins").Elements().Any())
                context.Result.NotImported.Add("User plugins (" + root.Element("plugins").Elements().Count().ToString(CultureInfo.InvariantCulture) + "): Python scripts cannot run in JoyPro; rebuild what they do with virtual functions.");
            context.StartMode = start;
        }

        private static GContainer ReadR13Container(XElement element, Context context)
        {
            GContainer container = new GContainer { Type = Attr(element, "type").Length > 0 ? Attr(element, "type").ToLowerInvariant().Replace('-', '_') : "basic" };
            foreach (XAttribute attribute in element.Attributes())
                container.Values[attribute.Name.LocalName] = attribute.Value;
            foreach (XElement child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "action-set":
                    {
                        List<GAction> set = new List<GAction>();
                        foreach (XElement action in child.Elements())
                        {
                            if (action.Name.LocalName == "activation-condition")
                            {
                                container.HasConditions = true;
                                continue;
                            }
                            set.Add(ReadR13Action(action));
                        }
                        container.Sets.Add(set);
                        break;
                    }
                    case "virtual-button":
                        ReadVirtualButton(child, container);
                        break;
                    case "activation-condition":
                        container.HasConditions = true;
                        break;
                }
            }
            return container;
        }

        private static GAction ReadR13Action(XElement element)
        {
            GAction action = new GAction { Type = element.Name.LocalName.ToLowerInvariant(), Element = element };
            foreach (XAttribute attribute in element.Attributes())
                action.Values[attribute.Name.LocalName] = attribute.Value;
            return action;
        }

        private static void ReadVirtualButton(XElement element, GContainer container)
        {
            double low;
            double high;
            if (double.TryParse(Attr(element, "lower-limit", "lower"), NumberStyles.Float, CultureInfo.InvariantCulture, out low) &&
                double.TryParse(Attr(element, "upper-limit", "upper"), NumberStyles.Float, CultureInfo.InvariantCulture, out high))
            {
                container.RangeLow = Math.Min(low, high);
                container.RangeHigh = Math.Max(low, high);
                string direction = Attr(element, "direction");
                if (direction.Length > 0 && !string.Equals(direction, "anywhere", StringComparison.OrdinalIgnoreCase))
                    container.Values["vb-direction"] = direction;
            }
            foreach (XElement child in element.Elements())
            {
                int index = Array.IndexOf(HatDirectionNames, child.Name.LocalName.ToLowerInvariant().Replace('_', '-'));
                if (index >= 0 && !container.HatDirections.Contains(index))
                    container.HatDirections.Add(index);
            }
            // R14 writes the directions as text: "north|east".
            foreach (string part in (element.Value ?? string.Empty).Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index = Array.IndexOf(HatDirectionNames, part.Trim().ToLowerInvariant().Replace('_', '-'));
                if (index >= 0 && !container.HatDirections.Contains(index))
                    container.HatDirections.Add(index);
            }
        }

        // ---------------------------------------------------------------------------------------------- R14

        private static readonly string[] R14Containers = { "tempo", "chain", "double-tap", "double_tap", "smart-toggle", "smart_toggle", "hat-buttons", "hat_buttons", "condition" };

        private static void ReadR14(XElement root, Context context)
        {
            Dictionary<string, XElement> library = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            XElement libraryNode = root.Element("library");
            if (libraryNode != null)
            {
                foreach (XElement action in libraryNode.Elements())
                {
                    string id = Attr(action, "id");
                    if (id.Length == 0)
                        id = Text(action.Element("id"));
                    if (id.Length > 0)
                        library[id] = action;
                }
            }
            XElement modes = root.Element("modes");
            if (modes != null)
            {
                foreach (XElement mode in modes.Elements("mode"))
                    context.AddMode(mode.Elements().Any() ? Text(mode.Element("name")) : mode.Value.Trim(), Attr(mode, "parent", "inherit").Length > 0 ? Attr(mode, "parent", "inherit") : Text(mode.Element("parent")));
            }
            XElement settings = root.Element("settings");
            context.StartMode = settings == null ? string.Empty : Text(settings.Element("startup-mode"));
            Dictionary<string, string> deviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XElement devices = root.Element("devices");
            if (devices != null)
            {
                foreach (XElement device in devices.Elements())
                {
                    string guid = Attr(device, "device-guid", "id", "guid");
                    if (guid.Length == 0)
                        guid = Text(device.Element("device-id"));
                    string name = Attr(device, "name").Length > 0 ? Attr(device, "name") : Text(device.Element("name"));
                    if (guid.Length > 0)
                        deviceNames[guid] = name;
                }
            }
            foreach (XElement input in root.Element("inputs") == null ? Enumerable.Empty<XElement>() : root.Element("inputs").Elements())
            {
                string guid = FirstText(input, "device-id", "device-guid", "device");
                string type = FirstText(input, "input-type", "type").ToLowerInvariant();
                string mode = FirstText(input, "mode");
                int id = Int(FirstText(input, "input-id", "id"), 0);
                if (string.Equals(guid, "keyboard", StringComparison.OrdinalIgnoreCase) || type == "key" || type == "keyboard")
                {
                    context.Result.NotImported.Add("Keyboard input " + FirstText(input, "input-id", "id") + " (mode " + mode + "): JoyPro's virtual functions read game controllers, not the keyboard.");
                    continue;
                }
                if (type != "axis" && type != "button" && type != "hat" || id <= 0)
                {
                    context.Result.NotImported.Add("Input \"" + type + "\" " + id.ToString(CultureInfo.InvariantCulture) + " of " + guid + ": not a joystick axis, button or hat.");
                    continue;
                }
                context.AddMode(mode, string.Empty);
                string name;
                deviceNames.TryGetValue(guid, out name);
                GBinding binding = new GBinding { DeviceGuid = guid, DeviceName = name ?? FirstText(input, "device-name"), Mode = mode, InputType = type, InputId = id, Description = FirstText(input, "description") };
                // One or more bindings (R14: action-configuration / binding), each with a root action and maybe a
                // virtual button.
                List<XElement> configurations = input.Elements().Where(e => e.Name.LocalName == "action-configuration" || e.Name.LocalName == "binding").ToList();
                if (configurations.Count == 0)
                    configurations.Add(input);
                foreach (XElement configuration in configurations)
                {
                    List<string> rootIds = configuration.Elements().Where(e => e.Name.LocalName == "root-action" || e.Name.LocalName == "action-id" || e.Name.LocalName == "action").Select(e => e.Value.Trim())
                        .Concat(configuration.Elements("actions").Elements().Select(e => e.Value.Trim())).Where(v => v.Length > 0).ToList();
                    GContainer basic = new GContainer();
                    basic.Sets.Add(new List<GAction>());
                    XElement virtualButton = configuration.Element("virtual-button");
                    if (virtualButton != null)
                        ReadVirtualButton(virtualButton, basic);
                    List<GContainer> made = new List<GContainer> { basic };
                    foreach (string rootId in rootIds)
                        ExpandR14(rootId, library, basic, made, 0, context, binding);
                    foreach (GContainer container in made.Where(c => c.Sets.Any(s => s.Count > 0)))
                    {
                        if (!ReferenceEquals(container, basic))
                        {
                            container.RangeLow = basic.RangeLow;
                            container.RangeHigh = basic.RangeHigh;
                            container.HatDirections = basic.HatDirections;
                        }
                        binding.Containers.Add(container);
                    }
                }
                if (binding.Containers.Count > 0)
                    context.Bindings.Add(binding);
            }
        }

        // An R14 library action into the model: a root (or description) passes its children on, a container-like action
        // (tempo, chain ...) becomes its own container, anything else goes into the binding's basic action set.
        private static void ExpandR14(string id, Dictionary<string, XElement> library, GContainer basic, List<GContainer> made, int depth, Context context, GBinding binding)
        {
            XElement element;
            if (depth > 16 || !library.TryGetValue(id, out element))
            {
                if (depth <= 16)
                    context.Result.NotImported.Add(Where(binding) + ": action " + id + " is not in the profile's library.");
                return;
            }
            GAction action = ReadR14Action(element);
            if (action.Type == "root" || action.Type == "description")
            {
                foreach (string child in R14Children(element, library))
                    ExpandR14(child, library, basic, made, depth + 1, context, binding);
                if (action.Type == "description" && action.Value("description").Length > 0 && binding.Description.Length == 0)
                    binding.Description = action.Value("description");
                return;
            }
            if (R14Containers.Contains(action.Type))
            {
                GContainer container = new GContainer { Type = action.Type.Replace('-', '_') };
                foreach (KeyValuePair<string, string> value in action.Values)
                    container.Values[value.Key] = value.Value;
                foreach (List<string> group in R14ChildGroups(element, library))
                {
                    GContainer inner = new GContainer();
                    inner.Sets.Add(new List<GAction>());
                    List<GContainer> innerMade = new List<GContainer>();
                    foreach (string child in group)
                        ExpandR14(child, library, inner, innerMade, depth + 1, context, binding);
                    container.Sets.Add(inner.Sets[0]);
                    if (innerMade.Count > 0)
                        context.Result.NotImported.Add(Where(binding) + ": a " + innerMade[0].Type + " inside a " + container.Type + " is not imported (one level of containers).");
                }
                if (container.Type == "condition")
                {
                    context.Result.NotImported.Add(Where(binding) + ": a condition action - its actions are imported to run always.");
                    foreach (GAction inner in container.Sets.SelectMany(s => s))
                        basic.Sets[0].Add(inner);
                    return;
                }
                made.Add(container);
                return;
            }
            basic.Sets[0].Add(action);
        }

        private static GAction ReadR14Action(XElement element)
        {
            GAction action = new GAction { Type = (Attr(element, "type").Length > 0 ? Attr(element, "type") : element.Name.LocalName).ToLowerInvariant().Replace('_', '-'), Element = element };
            foreach (XAttribute attribute in element.Attributes())
                action.Values[attribute.Name.LocalName] = attribute.Value;
            foreach (XElement property in element.Descendants("property"))
            {
                string name = Text(property.Element("name"));
                if (name.Length == 0)
                    name = Attr(property, "name");
                XElement value = property.Element("value");
                if (name.Length > 0)
                    action.Values[name] = value == null ? property.Value.Trim() : value.Value.Trim();
            }
            // Plain child elements with text (<vjoy-device-id>1</vjoy-device-id>) count as values too.
            foreach (XElement child in element.Elements().Where(e => !e.HasElements && e.Name.LocalName != "property"))
            {
                if (!action.Values.ContainsKey(child.Name.LocalName))
                    action.Values[child.Name.LocalName] = child.Value.Trim();
            }
            return action;
        }

        // The ids of the actions a root/description action holds (any text that is a library id, in document order).
        private static List<string> R14Children(XElement element, Dictionary<string, XElement> library)
        {
            string self = Attr(element, "id");
            return element.Descendants().Where(e => !e.HasElements)
                .SelectMany(e => e.Value.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(v => v.Trim()).Where(v => v.Length > 0 && library.ContainsKey(v) && !string.Equals(v, self, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A container's action groups: per child element that holds library ids (short-actions, long-actions, or one
        // group per chain entry), in document order.
        private static List<List<string>> R14ChildGroups(XElement element, Dictionary<string, XElement> library)
        {
            List<List<string>> groups = new List<List<string>>();
            string self = Attr(element, "id");
            foreach (XElement child in element.Elements())
            {
                List<string> ids = (child.HasElements ? child.Descendants().Where(e => !e.HasElements) : new[] { child })
                    .SelectMany(e => e.Value.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)).Select(v => v.Trim())
                    .Where(v => v.Length > 0 && library.ContainsKey(v) && !string.Equals(v, self, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ids.Count == 0)
                    continue;
                // A chain lists its entries in one element: one group per entry.
                string name = child.Name.LocalName;
                if (child.Elements().Count() > 1 && child.Elements().All(e => !e.HasElements) && (name.Contains("chain") || name == "actions" || name == "children"))
                    groups.AddRange(ids.Select(i => new List<string> { i }));
                else
                    groups.Add(ids);
            }
            return groups;
        }

        // ---------------------------------------------------------------------------------------------- conversion

        private sealed class Context
        {
            private readonly IList<string> known;
            private readonly int vjoyTarget;
            private readonly HashSet<string> used;
            private int nextId = 1;
            private int firstVJoy = -1;
            public readonly GremlinImportResult Result = new GremlinImportResult();
            public readonly List<GBinding> Bindings = new List<GBinding>();
            public readonly List<GMerge> Merges = new List<GMerge>();
            public string StartMode = string.Empty;
            private readonly Dictionary<string, string> modeParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> modeOrder = new List<string>();

            public Context(IList<string> knownDevices, int vjoyDevice, ICollection<string> usedIds)
            {
                known = knownDevices ?? new List<string>();
                vjoyTarget = vjoyDevice;
                used = new HashSet<string>(usedIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }

            public void AddMode(string name, string parent)
            {
                name = (name ?? string.Empty).Trim();
                if (name.Length == 0)
                    return;
                if (!modeOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
                    modeOrder.Add(name);
                parent = (parent ?? string.Empty).Trim();
                string existing;
                if (parent.Length > 0 || !modeParents.TryGetValue(name, out existing))
                    modeParents[name] = parent;
            }

            private bool UsesModes
            {
                get { return modeOrder.Count > 1; }
            }

            public void Convert()
            {
                if (UsesModes)
                {
                    foreach (string mode in modeOrder)
                    {
                        string parent;
                        modeParents.TryGetValue(mode, out parent);
                        Result.Modes.Add(new VfMode { Name = mode, Parent = parent ?? string.Empty });
                    }
                    VfModeSet set = new VfModeSet { Modes = Result.Modes, Start = StartMode };
                    set.Normalize();
                    Result.Modes = set.Modes;
                    Result.StartMode = set.Start;
                }
                foreach (GBinding binding in Bindings)
                    ConvertBinding(binding);
                foreach (GMerge merge in Merges)
                    ConvertMerge(merge);
                if (vjoyTarget > 0 && Result.VJoyDevices.Count > 1)
                    Result.Notes.Add("The profile writes to " + Result.VJoyDevices.Count.ToString(CultureInfo.InvariantCulture) + " vJoy devices; they moved together so its first one is vJoy " + vjoyTarget.ToString(CultureInfo.InvariantCulture) + ".");
            }

            // ---- devices

            public string Device(string guid, string name)
            {
                GremlinDeviceMatch match = Result.Devices.FirstOrDefault(d => string.Equals(d.GremlinGuid, guid, StringComparison.OrdinalIgnoreCase) && string.Equals(d.GremlinName, name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.JoyProDevice;
                match = new GremlinDeviceMatch { GremlinGuid = guid ?? string.Empty, GremlinName = name ?? string.Empty, Match = "none" };
                Guid parsed;
                string cleanGuid = (guid ?? string.Empty).Trim().Trim('{', '}');
                bool hasGuid = Guid.TryParse(cleanGuid, out parsed);
                string byGuid = hasGuid ? known.FirstOrDefault(k => k.IndexOf(parsed.ToString("D"), StringComparison.OrdinalIgnoreCase) >= 0) : null;
                if (byGuid != null)
                {
                    match.JoyProDevice = byGuid;
                    match.Match = "guid";
                }
                else
                {
                    List<string> byName = string.IsNullOrWhiteSpace(name) ? new List<string>() : known.Where(k => string.Equals(NameOf(k), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byName.Count > 0)
                    {
                        match.JoyProDevice = byName[0];
                        match.Match = "name";
                        if (byName.Count > 1)
                            Result.Notes.Add("\"" + name + "\" matches " + byName.Count.ToString(CultureInfo.InvariantCulture) + " devices by name; the first was taken (" + byName[0] + "). Check its functions' inputs.");
                    }
                    else
                    {
                        // Not here now: kept by the profile's name and GUID (works when the GUID is the device's
                        // DirectInput instance GUID, as Gremlin R13 stores it).
                        match.JoyProDevice = (string.IsNullOrWhiteSpace(name) ? "Gremlin device" : name.Trim()) + " {" + (hasGuid ? parsed.ToString("D").ToUpperInvariant() : cleanGuid) + "}";
                        Result.NotImported.Add("Device \"" + (name ?? guid) + "\" " + (guid ?? string.Empty) + " is not connected and not in the profile: its functions are imported on " + match.JoyProDevice + " - plug it in, or exchange the device in JoyPro.");
                    }
                }
                Result.Devices.Add(match);
                return match.JoyProDevice;
            }

            private static string NameOf(string joyProName)
            {
                int brace = joyProName.LastIndexOf(" {", StringComparison.Ordinal);
                return brace > 0 ? joyProName.Substring(0, brace).Trim() : joyProName.Trim();
            }

            public int VJoy(string raw)
            {
                int id = Int(raw, 1);
                if (id < 1 || id > 16)
                    id = 1;
                if (!Result.VJoyDevices.Contains(id))
                    Result.VJoyDevices.Add(id);
                if (vjoyTarget <= 0)
                    return id;
                if (firstVJoy < 0)
                    firstVJoy = Result.VJoyDevices.Min();
                int moved = id - firstVJoy + vjoyTarget;
                return moved < 1 || moved > 16 ? id : moved;
            }

            // ---- functions

            private string NewId()
            {
                while (true)
                {
                    string id = "f" + (nextId++).ToString(CultureInfo.InvariantCulture);
                    if (used.Add(id))
                        return id;
                }
            }

            private VirtualFunction Add(string type, string name, GBinding binding, string origin)
            {
                VirtualFunction function = VirtualFunctionCatalog.Create(type, name);
                function.Inputs.Clear();
                function.Outputs.Clear();
                function.Id = NewId();
                if (UsesModes && binding != null && binding.Mode.Length > 0)
                    function.Modes.Add(binding.Mode);
                Result.Functions.Add(function);
                Result.Origins[function.Id] = origin;
                return function;
            }

            private static void SetInput(VirtualFunction function, string role, Source source)
            {
                function.Inputs.Add(new VfInput { Role = role, Device = source.Device, Input = source.Input });
            }

            private static string InputLabel(string type, int id)
            {
                if (type == "axis")
                    return "axis " + id.ToString(CultureInfo.InvariantCulture);
                return (type == "hat" ? "hat " : "button ") + id.ToString(CultureInfo.InvariantCulture);
            }

            private string ShortName(GBinding binding)
            {
                string name = (binding.DeviceName ?? string.Empty).Trim();
                if (name.Length > 24)
                    name = name.Substring(0, 24).Trim();
                return (name.Length > 0 ? name + " " : string.Empty) + InputLabel(binding.InputType, binding.InputId);
            }

            private void ConvertBinding(GBinding binding)
            {
                string where = Where(binding);
                string device = Device(binding.DeviceGuid, binding.DeviceName);
                string token = PhysicalToken(binding.InputType, binding.InputId, where);
                if (token.Length == 0)
                    return;
                foreach (GContainer container in binding.Containers)
                {
                    if (container.HasConditions)
                        Result.NotImported.Add(where + ": activation conditions - the actions are imported to run always.");
                    string baseName = binding.Description.Length > 0 ? binding.Description : ShortName(binding);
                    Source source = new Source { Device = device, Input = token, Kind = binding.InputType == "hat" ? "hat" : binding.InputType == "axis" ? "axis" : "button" };
                    // A range of an axis or directions of a hat used as a button.
                    if (binding.InputType == "axis" && !double.IsNaN(container.RangeLow))
                        source = AxisRange(binding, container, source, baseName, where);
                    else if (binding.InputType == "hat" && container.HatDirections.Count > 0 && container.Type != "hat_buttons")
                    {
                        if (container.HatDirections.Count > 1)
                            Result.Notes.Add(where + ": the button is the hat direction " + HatDirectionNames[container.HatDirections[0]] + " only (Gremlin had " + string.Join(", ", container.HatDirections.Select(d => HatDirectionNames[d])) + ").");
                        source = new Source { Device = device, Input = "JOY_BTN_POV" + binding.InputId.ToString(CultureInfo.InvariantCulture) + "_" + VfTokens.HatDirections[container.HatDirections[0]], Kind = "button" };
                    }
                    if (container.Value("vb-direction").Length > 0)
                        Result.Notes.Add(where + ": the axis range fires in both directions (Gremlin: only when entered from " + container.Value("vb-direction") + ").");
                    switch (container.Type)
                    {
                        case "basic":
                            ConvertSet(container.Sets.FirstOrDefault() ?? new List<GAction>(), source, baseName, binding, where);
                            break;
                        case "tempo":
                            Presses(container, source, baseName, binding, where, false);
                            break;
                        case "double_tap":
                            Presses(container, source, baseName, binding, where, true);
                            break;
                        case "chain":
                            Chain(container, source, baseName, binding, where);
                            break;
                        case "smart_toggle":
                            SmartToggle(container, source, baseName, binding, where);
                            break;
                        case "hat_buttons":
                            HatButtons(container, source, baseName, binding, where);
                            break;
                        default:
                            Result.NotImported.Add(where + ": the container \"" + container.Type + "\" is not known to JoyPro (" + container.Sets.Sum(s => s.Count).ToString(CultureInfo.InvariantCulture) + " action(s) left out).");
                            break;
                    }
                }
            }

            private string PhysicalToken(string type, int id, string where)
            {
                if (type == "axis")
                {
                    if (id < 1 || id > 8)
                    {
                        Result.NotImported.Add(where + ": axis " + id.ToString(CultureInfo.InvariantCulture) + " - JoyPro reads 8 axes per device.");
                        return string.Empty;
                    }
                    return AxisTokens[id - 1];
                }
                if (type == "hat")
                {
                    if (id < 1 || id > 4)
                    {
                        Result.NotImported.Add(where + ": hat " + id.ToString(CultureInfo.InvariantCulture) + " - JoyPro reads 4 hats per device.");
                        return string.Empty;
                    }
                    return "POV" + id.ToString(CultureInfo.InvariantCulture);
                }
                if (id < 1 || id > 128)
                {
                    Result.NotImported.Add(where + ": button " + id.ToString(CultureInfo.InvariantCulture) + " - JoyPro reads 128 buttons per device.");
                    return string.Empty;
                }
                return "JOY_BTN" + id.ToString(CultureInfo.InvariantCulture);
            }

            // An axis range used as a button: a band function whose band is the range; its output feeds the actions.
            private Source AxisRange(GBinding binding, GContainer container, Source axis, string baseName, string where)
            {
                double low = VfRuntime.Clamp01((container.RangeLow + 1) / 2);
                double high = VfRuntime.Clamp01((container.RangeHigh + 1) / 2);
                List<double> edges = new List<double>();
                if (low > 0.005)
                    edges.Add(Math.Round(low, 4));
                if (high < 0.995 && high > low + 0.005)
                    edges.Add(Math.Round(high, 4));
                int band = low > 0.005 ? 2 : 1;
                if (edges.Count == 0)
                {
                    Result.Notes.Add(where + ": the axis range covers the whole travel: it is always pressed.");
                    edges.Add(0.5);
                    band = 1;
                }
                VirtualFunction bands = Add(VirtualFunctionCatalog.Bands, baseName + " range", binding, where + " (axis range " + container.RangeLow.ToString("0.###", CultureInfo.InvariantCulture) + " .. " + container.RangeHigh.ToString("0.###", CultureInfo.InvariantCulture) + ")");
                bands.Edges = edges;
                SetInput(bands, "in", axis);
                for (int i = 1; i <= edges.Count + 1; i++)
                    bands.Outputs.Add(new VfOutput { Role = "band" + i.ToString(CultureInfo.InvariantCulture), Mode = VirtualFunctionCatalog.ModeHold });
                return new Source { Device = VfChain.Device(bands.Id), Input = "BAND" + band.ToString(CultureInfo.InvariantCulture), Kind = "button" };
            }

            // ---- action sets

            // One action set on a source: remaps and keys together in one remap function, a response curve with an axis
            // remap as a curve function, macros and mode changes as their own functions.
            private void ConvertSet(List<GAction> actions, Source source, string name, GBinding binding, string where)
            {
                List<VfOutput> outputs = new List<VfOutput>();
                bool keys = false;
                bool invert = false;
                GAction curve = actions.FirstOrDefault(a => a.Type == "response-curve");
                foreach (GAction action in actions)
                {
                    switch (action.Type)
                    {
                        case "remap":
                        {
                            VfOutput output = RemapTarget(action, source, where);
                            if (output != null)
                                outputs.Add(output);
                            break;
                        }
                        case "map-to-keyboard":
                        case "map-to-keyboard-ex":
                        {
                            List<string> names = Keys(action, where);
                            foreach (string key in names)
                                outputs.Add(new VfOutput { Target = key });
                            keys |= names.Count > 0;
                            break;
                        }
                        case "response-curve":
                            break;
                        case "macro":
                            Macro(action, source, name, binding, where);
                            break;
                        case "switch-mode":
                        case "temporary-mode-switch":
                        case "cycle-modes":
                        case "previous-mode":
                        case "change-mode":
                            ModeChange(action, source, name, binding, where);
                            break;
                        case "description":
                            break;
                        case "split-axis":
                        case "split":
                            Result.NotImported.Add(where + ": split axis - use JoyPro's \"Split an axis\" function on the same axis.");
                            break;
                        default:
                            Result.NotImported.Add(where + ": the action \"" + action.Type + "\" " + WhyNot(action.Type));
                            break;
                    }
                }
                if (curve != null && source.Kind != "axis")
                    Result.NotImported.Add(where + ": a response curve on a button does nothing in JoyPro.");
                if (outputs.Count == 0)
                {
                    if (curve != null && source.Kind == "axis")
                        Result.NotImported.Add(where + ": a response curve without a remap - JoyPro forwards nothing to shape (add a remap to a vJoy axis).");
                    return;
                }
                VirtualFunction function;
                if (curve != null && source.Kind == "axis" && outputs.Count == 1 && VfTokens.OutputClass(KindOf(outputs[0].Target)) == "axis")
                {
                    function = Add(VirtualFunctionCatalog.Curve, name, binding, where);
                    SetInput(function, "in", source);
                    function.Tuning = CurveTuning(curve, where, ref invert);
                    outputs[0].Role = "out";
                    function.Outputs.Add(outputs[0]);
                    return;
                }
                if (curve != null && source.Kind == "axis")
                    Result.Notes.Add(where + ": the response curve applies to the first axis output only - JoyPro made the others plain remaps.");
                function = Add(VirtualFunctionCatalog.Remap, name, binding, where);
                SetInput(function, "in", source);
                function.Keyboard = keys;
                for (int i = 0; i < outputs.Count; i++)
                {
                    outputs[i].Role = "out" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    function.Outputs.Add(outputs[i]);
                }
                if (outputs.Count > 8)
                {
                    Result.NotImported.Add(where + ": more than 8 outputs on one input - the first 8 were kept.");
                    function.Outputs.RemoveRange(8, function.Outputs.Count - 8);
                }
            }

            private VfOutput RemapTarget(GAction action, Source source, string where)
            {
                string device = action.Value("vjoy", "vjoy-id", "vjoy-device-id", "device");
                string axis = action.Value("axis", "vjoy-axis");
                string button = action.Value("button", "vjoy-button");
                string hat = action.Value("hat", "vjoy-hat");
                string type = action.Value("vjoy-input-type", "input-type").ToLowerInvariant();
                string id = action.Value("vjoy-input-id", "input-id");
                if (type == "axis" && axis.Length == 0)
                    axis = id;
                else if (type == "button" && button.Length == 0)
                    button = id;
                else if (type == "hat" && hat.Length == 0)
                    hat = id;
                string mode = action.Value("axis-type", "axis-mode").ToLowerInvariant();
                if (mode == "relative")
                    Result.Notes.Add(where + ": a relative axis remap became an absolute one (JoyPro forwards axes as they are; \"Buttons to axis\" or \"Axis to encoder pulses\" may do what you need).");
                double scaling;
                if (double.TryParse(action.Value("axis-scaling"), NumberStyles.Float, CultureInfo.InvariantCulture, out scaling) && Math.Abs(scaling - 1) > 1e-6)
                    Result.Notes.Add(where + ": axis scaling " + scaling.ToString("0.###", CultureInfo.InvariantCulture) + " is left out (JoyPro forwards the full axis).");
                int vjoy = VJoy(device);
                int number = Int(axis.Length > 0 ? axis : button.Length > 0 ? button : hat, 0);
                if (axis.Length > 0)
                {
                    if (number < 1 || number > 8)
                    {
                        Result.NotImported.Add(where + ": vJoy axis " + axis + " - vJoy has 8 axes.");
                        return null;
                    }
                    return new VfOutput { Target = AxisTokens[number - 1], VJoyDevice = vjoy };
                }
                if (hat.Length > 0)
                {
                    if (number < 1 || number > 4)
                    {
                        Result.NotImported.Add(where + ": vJoy hat " + hat + " - vJoy has 4 hats.");
                        return null;
                    }
                    return new VfOutput { Target = "POV" + number.ToString(CultureInfo.InvariantCulture), VJoyDevice = vjoy };
                }
                if (button.Length > 0)
                {
                    if (number < 1 || number > 128)
                    {
                        Result.NotImported.Add(where + ": vJoy button " + button + " - vJoy has 128 buttons.");
                        return null;
                    }
                    return new VfOutput { Target = "JOY_BTN" + number.ToString(CultureInfo.InvariantCulture), VJoyDevice = vjoy };
                }
                Result.NotImported.Add(where + ": a remap without a vJoy input.");
                return null;
            }

            private static string KindOf(string target)
            {
                string kind;
                int index;
                int mask;
                return VfTokens.TryParseOutput(target, out kind, out index, out mask) ? kind : "button";
            }

            private List<string> Keys(GAction action, string where)
            {
                List<string> names = new List<string>();
                IEnumerable<XElement> keys = action.Element == null ? Enumerable.Empty<XElement>() : action.Element.Descendants("key");
                foreach (XElement key in keys)
                {
                    string name = KeyName(Attr(key, "scan-code", "scan_code", "code"), Attr(key, "extended"), where);
                    if (name.Length > 0 && !names.Contains(name))
                        names.Add(name);
                }
                // R14: keys in a property ("30:False|42:False").
                foreach (string part in action.Value("keys").Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] bits = part.Split(':');
                    string name = KeyName(bits[0], bits.Length > 1 ? bits[1] : "false", where);
                    if (name.Length > 0 && !names.Contains(name))
                        names.Add(name);
                }
                return names;
            }

            private string KeyName(string scanText, string extendedText, string where)
            {
                int scan = Int(scanText, -1);
                if (scan <= 0)
                    return string.Empty;
                bool extended = string.Equals(extendedText, "true", StringComparison.OrdinalIgnoreCase) || extendedText == "1";
                string name = VfTokens.KeyName(scan + (extended ? 0x100 : 0));
                if (name.Length == 0)
                    Result.NotImported.Add(where + ": the key with scan code " + scan.ToString(CultureInfo.InvariantCulture) + (extended ? " (extended)" : string.Empty) + " is not in JoyPro's key list.");
                return name;
            }

            private static string WhyNot(string type)
            {
                switch (type)
                {
                    case "map-to-mouse":
                    case "map-to-mouse-ex":
                        return "- JoyPro's virtual functions send no mouse input.";
                    case "play-sound":
                    case "text-to-speech":
                        return "- JoyPro plays no sounds or speech for inputs.";
                    case "pause-action":
                    case "resume-action":
                    case "toggle-pause-resume-action":
                        return "- the forwarder has no pause; Stop on the Virtual Joystick page stops it.";
                    case "split-axis":
                        return "- use JoyPro's \"Split an axis\" function.";
                    default:
                        return "is not known to JoyPro.";
                }
            }

            // ---- response curve

            private string CurveTuning(GAction action, string where, ref bool inverted)
            {
                AxisTuning tuning = new AxisTuning();
                // Dead zone "low centre-low centre-high high" (-1..1).
                double[] dz = (action.Value("deadzone").Length > 0 ? action.Value("deadzone") : string.Join(" ", new[] { "deadzone-low", "deadzone-center-low", "deadzone-center-high", "deadzone-high" }.Select(k => action.Value(k))))
                    .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => { double d; return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN; }).ToArray();
                if (dz.Length == 4 && dz.All(d => !double.IsNaN(d)))
                {
                    tuning.Deadzone = Math.Round(Math.Max(0, Math.Min(0.5, (dz[2] - dz[1]) / 2)), 3);
                    tuning.SaturationX = Math.Round(Math.Max(0.3, Math.Min(1, (dz[3] - dz[0]) / 2)), 3);
                    if (Math.Abs(dz[1] + dz[2]) > 0.01 || Math.Abs(dz[0] + dz[3]) > 0.01)
                        Result.Notes.Add(where + ": the dead zones were not symmetric; JoyPro uses one centre dead zone and one saturation for both sides.");
                }
                List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
                string type = string.Empty;
                XElement mapping = action.Element == null ? null : action.Element.Descendants("mapping").FirstOrDefault();
                if (mapping != null)
                    type = Attr(mapping, "type");
                List<XElement> controls = action.Element == null ? new List<XElement>() : action.Element.Descendants("control-point").ToList();
                foreach (XElement point in controls)
                {
                    double x;
                    double y;
                    if (double.TryParse(Attr(point, "x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(Attr(point, "y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        points.Add(new KeyValuePair<double, double>(x, y));
                }
                // R14: "x,y|x,y".
                foreach (string pair in action.Value("control-points", "curve").Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] xy = pair.Split(',');
                    double x;
                    double y;
                    if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        points.Add(new KeyValuePair<double, double>(x, y));
                }
                if (type.IndexOf("bezier", StringComparison.OrdinalIgnoreCase) >= 0 && points.Count >= 4)
                {
                    // Centre point, handle, handle, centre point ...: the centres carry the curve.
                    points = points.Where((p, i) => i % 3 == 0).ToList();
                    Result.Notes.Add(where + ": the Bezier curve is followed through its points (the handles' bends are left out).");
                }
                points = points.OrderBy(p => p.Key).ToList();
                if (points.Count >= 2)
                {
                    Func<double, double> at = x => Interpolate(points, x);
                    if (at(1) < at(-1))
                    {
                        tuning.Inverted = true;
                        inverted = true;
                    }
                    double sign = tuning.Inverted ? -1 : 1;
                    List<double> user = new List<double>();
                    double asymmetry = 0;
                    for (int i = 0; i < AxisModels.UserCurvePoints; i++)
                    {
                        double x = i / (double)(AxisModels.UserCurvePoints - 1);
                        double up = sign * at(x);
                        double down = -sign * at(-x);
                        asymmetry = Math.Max(asymmetry, Math.Abs(up - down));
                        user.Add(Math.Round(Math.Max(0, Math.Min(1, (up + down) / 2)) * 100, 1));
                    }
                    bool linear = user.Select((v, i) => Math.Abs(v - i * 10)).Max() < 0.6;
                    if (!linear)
                        tuning.Curvature = user;
                    if (asymmetry > 0.02)
                        Result.Notes.Add(where + ": the curve differs between the two halves; JoyPro's curve is the same on both sides (the average was taken).");
                }
                return tuning.Format();
            }

            private static double Interpolate(List<KeyValuePair<double, double>> points, double x)
            {
                if (x <= points[0].Key)
                    return points[0].Value;
                for (int i = 1; i < points.Count; i++)
                {
                    if (x <= points[i].Key)
                    {
                        double span = points[i].Key - points[i - 1].Key;
                        double t = span <= 1e-9 ? 1 : (x - points[i - 1].Key) / span;
                        return points[i - 1].Value + (points[i].Value - points[i - 1].Value) * t;
                    }
                }
                return points[points.Count - 1].Value;
            }

            // ---- macro

            private void Macro(GAction action, Source source, string name, GBinding binding, string where)
            {
                VirtualFunction macro = Add(VirtualFunctionCatalog.Macro, name + " macro", binding, where + " (macro)");
                SetInput(macro, "trigger", source);
                macro.Steps.Clear();
                Dictionary<string, string> roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                double pendingMs = 0;
                XElement actions = action.Element == null ? null : action.Element.Element("actions") ?? action.Element;
                foreach (XElement step in actions == null ? Enumerable.Empty<XElement>() : actions.Elements())
                {
                    switch (step.Name.LocalName)
                    {
                        case "pause":
                        {
                            double seconds;
                            if (double.TryParse(Attr(step, "duration", "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                                pendingMs += seconds * 1000;
                            break;
                        }
                        case "key":
                        {
                            string key = KeyName(Attr(step, "scan-code", "scan_code", "code"), Attr(step, "extended"), where);
                            if (key.Length == 0)
                                break;
                            macro.Keyboard = true;
                            macro.Steps.Add(new VfStep { Output = key, Action = IsTrue(Attr(step, "press", "value")) ? "press" : "release", DelayMs = (int)Math.Round(pendingMs), HoldMs = 0 });
                            pendingMs = 0;
                            break;
                        }
                        case "vjoy":
                        {
                            string type = Attr(step, "input-type").ToLowerInvariant();
                            if (type != "button")
                            {
                                Result.NotImported.Add(where + ": a macro step on a vJoy " + (type.Length > 0 ? type : "input") + " - JoyPro's macros press buttons and keys.");
                                break;
                            }
                            int number = Int(Attr(step, "input-id", "id"), 0);
                            if (number < 1 || number > 128)
                                break;
                            int vjoy = VJoy(Attr(step, "vjoy-id", "vjoy", "device"));
                            string key = vjoy.ToString(CultureInfo.InvariantCulture) + ":" + number.ToString(CultureInfo.InvariantCulture);
                            string role;
                            if (!roles.TryGetValue(key, out role))
                            {
                                if (roles.Count >= 16)
                                {
                                    Result.NotImported.Add(where + ": a macro with more than 16 vJoy buttons - the rest left out.");
                                    break;
                                }
                                role = "out" + (roles.Count + 1).ToString(CultureInfo.InvariantCulture);
                                roles[key] = role;
                                macro.Outputs.Add(new VfOutput { Role = role, Target = "JOY_BTN" + number.ToString(CultureInfo.InvariantCulture), VJoyDevice = vjoy });
                            }
                            macro.Steps.Add(new VfStep { Output = role, Action = IsTrue(Attr(step, "value", "press")) ? "press" : "release", DelayMs = (int)Math.Round(pendingMs), HoldMs = 0 });
                            pendingMs = 0;
                            break;
                        }
                        case "mouse":
                        case "mouse-motion":
                            Result.NotImported.Add(where + ": a macro step on the mouse - JoyPro sends no mouse input.");
                            break;
                        default:
                            Result.NotImported.Add(where + ": the macro step \"" + step.Name.LocalName + "\" is not known to JoyPro.");
                            break;
                    }
                }
                if (macro.Outputs.Count == 0)
                    macro.Outputs.Add(new VfOutput { Role = "out1" });
                XElement repeat = action.Element == null ? null : action.Element.Descendants("repeat").FirstOrDefault();
                string repeatType = repeat == null ? action.Value("repeat", "repeat-type") : Attr(repeat, "type");
                switch (repeatType.ToLowerInvariant())
                {
                    case "hold":
                        macro.Params["repeat"] = 1;
                        break;
                    case "toggle":
                        Result.Notes.Add(where + ": the macro repeats while the button is held (Gremlin: from one press to the next).");
                        macro.Params["repeat"] = 1;
                        break;
                    case "count":
                        Result.Notes.Add(where + ": the macro plays once per press (Gremlin played it " + (repeat == null ? "several" : Attr(repeat, "count")) + " times).");
                        macro.Params["repeat"] = 0;
                        break;
                    default:
                        macro.Params["repeat"] = 0;
                        break;
                }
                if (macro.Steps.Count == 0)
                    Result.NotImported.Add(where + ": the macro has no step JoyPro can play.");
                if (macro.Steps.Any(s => s.Output.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase)))
                    Result.Notes.Add(where + ": the macro types keys; JoyPro sends them only while the virtual joystick runs for real (Start).");
            }

            private static bool IsTrue(string text)
            {
                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
            }

            // ---- mode changes

            private void ModeChange(GAction action, Source source, string name, GBinding binding, string where)
            {
                int kind;
                List<string> targets = new List<string>();
                string type = action.Type;
                if (type == "change-mode")
                {
                    string change = action.Value("change-type", "type-of-change", "change").ToLowerInvariant();
                    type = change.Contains("temp") ? "temporary-mode-switch" : change.Contains("cycle") ? "cycle-modes" : change.Contains("previous") || change.Contains("unwind") ? "previous-mode" : "switch-mode";
                    targets.AddRange(action.Value("target-mode", "target-modes", "mode", "name").Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
                    if (action.Element != null)
                        targets.AddRange(action.Element.Elements("mode").Select(m => Attr(m, "name").Length > 0 ? Attr(m, "name") : m.Value.Trim()));
                }
                switch (type)
                {
                    case "temporary-mode-switch":
                        kind = VirtualFunctionCatalog.ModeActionHold;
                        break;
                    case "cycle-modes":
                        kind = VirtualFunctionCatalog.ModeActionCycle;
                        if (action.Element != null)
                            targets.AddRange(action.Element.Descendants("mode").Select(m => Attr(m, "name").Length > 0 ? Attr(m, "name") : m.Value.Trim()));
                        break;
                    case "previous-mode":
                        kind = VirtualFunctionCatalog.ModeActionPrevious;
                        break;
                    default:
                        kind = VirtualFunctionCatalog.ModeActionSwitch;
                        break;
                }
                if (action.Type != "change-mode" && kind != VirtualFunctionCatalog.ModeActionCycle)
                    targets.Add(action.Value("name", "mode", "target-mode"));
                targets = targets.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (source.Kind != "button")
                {
                    Result.NotImported.Add(where + ": a mode change on an " + source.Kind + " - JoyPro switches modes with a button (or an axis range).");
                    return;
                }
                VirtualFunction function = Add(VirtualFunctionCatalog.ModeSwitch, name + " mode", binding, where + " (" + type + ")");
                SetInput(function, "in", source);
                function.Params["action"] = kind;
                function.SwitchModes = targets;
                foreach (string target in targets.Where(t => !modeOrder.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    Result.NotImported.Add(where + ": the mode \"" + target + "\" is not defined in the profile.");
                if (!UsesModes)
                    Result.Notes.Add(where + ": a mode change in a profile with one mode does nothing.");
            }

            // ---- containers

            // An action set as one output of a container function: a lone vJoy button remap goes straight on the
            // output; anything else is left internal and the set's actions read it (function chaining).
            private void ContainerOutput(VirtualFunction container, string role, List<GAction> set, string name, GBinding binding, string where)
            {
                VfOutput output = new VfOutput { Role = role };
                container.Outputs.Add(output);
                if (set == null || set.Count == 0)
                    return;
                if (set.Count == 1 && set[0].Type == "remap")
                {
                    VfOutput target = RemapTarget(set[0], new Source { Kind = "button" }, where);
                    if (target != null && VfTokens.OutputClass(KindOf(target.Target)) == "button")
                    {
                        output.Target = target.Target;
                        output.VJoyDevice = target.VJoyDevice;
                        return;
                    }
                }
                ConvertSet(set, new Source { Device = VfChain.Device(container.Id), Input = role.ToUpperInvariant(), Kind = "button" }, name, binding, where);
            }

            private void Presses(GContainer container, Source source, string name, GBinding binding, string where, bool doubleTap)
            {
                if (source.Kind != "button")
                {
                    Result.NotImported.Add(where + ": a " + container.Type + " container on an " + source.Kind + " - JoyPro times button presses only.");
                    return;
                }
                VirtualFunction function = Add(VirtualFunctionCatalog.Presses, name + (doubleTap ? " (double tap)" : " (short/long)"), binding, where + " (" + container.Type + ")");
                SetInput(function, "in", source);
                double delay;
                if (double.TryParse(container.Value("delay", "threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out delay) && delay > 0)
                    function.Params[doubleTap ? "doubleMs" : "longMs"] = Math.Max(doubleTap ? 100 : 150, Math.Min(doubleTap ? 1000 : 3000, Math.Round(delay * 1000)));
                function.Params["longHold"] = 1;
                ContainerOutput(function, "short", container.Sets.ElementAtOrDefault(0), name + " short", binding, where + " short");
                ContainerOutput(function, doubleTap ? "double" : "long", container.Sets.ElementAtOrDefault(1), name + (doubleTap ? " double" : " long"), binding, where + (doubleTap ? " double" : " long"));
                string activate = container.Value("activate-on", "activate_on").ToLowerInvariant();
                if (activate == "press" && !doubleTap)
                    Result.Notes.Add(where + ": the short action fires when the button is let go (Gremlin: on press).");
                if (doubleTap && (activate == "combined" || activate == "both"))
                    Result.Notes.Add(where + ": a double tap gives only the double action (Gremlin: both).");
                if (container.Sets.Count > 2)
                    Result.NotImported.Add(where + ": the " + container.Type + " container has more than two action sets - the rest left out.");
            }

            private void Chain(GContainer container, Source source, string name, GBinding binding, string where)
            {
                if (source.Kind != "button")
                {
                    Result.NotImported.Add(where + ": a chain container on an " + source.Kind + " - JoyPro steps through a chain with a button.");
                    return;
                }
                int count = Math.Min(32, container.Sets.Count);
                if (count < 2)
                {
                    ConvertSet(container.Sets.FirstOrDefault() ?? new List<GAction>(), source, name, binding, where);
                    return;
                }
                VirtualFunction function = Add(VirtualFunctionCatalog.Cycle, name + " (chain)", binding, where + " (chain)");
                SetInput(function, "next", source);
                function.Params["hold"] = 0;
                function.Params["wrap"] = 1;
                for (int i = 0; i < count; i++)
                    ContainerOutput(function, "step" + (i + 1).ToString(CultureInfo.InvariantCulture), container.Sets[i], name + " step " + (i + 1).ToString(CultureInfo.InvariantCulture), binding, where + " step " + (i + 1).ToString(CultureInfo.InvariantCulture));
                double timeout;
                if (double.TryParse(container.Value("timeout"), NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) && timeout > 0)
                    Result.NotImported.Add(where + ": the chain's timeout (" + timeout.ToString("0.##", CultureInfo.InvariantCulture) + " s back to the first entry) - JoyPro's cycle stays where it is.");
                Result.Notes.Add(where + ": each chain step is a pulse of " + function.Param("pulseMs", 80).ToString(CultureInfo.InvariantCulture) + " ms (Gremlin held it while the button was held).");
                if (container.Sets.Count > 32)
                    Result.NotImported.Add(where + ": a chain of more than 32 entries - the rest left out.");
            }

            private void SmartToggle(GContainer container, Source source, string name, GBinding binding, string where)
            {
                if (source.Kind != "button")
                {
                    Result.NotImported.Add(where + ": a smart toggle on an " + source.Kind + ".");
                    return;
                }
                VirtualFunction function = Add(VirtualFunctionCatalog.Toggle, name + " (toggle)", binding, where + " (smart toggle)");
                SetInput(function, "in", source);
                ContainerOutput(function, "out", container.Sets.FirstOrDefault(), name, binding, where);
                Result.Notes.Add(where + ": the smart toggle latches on every press (Gremlin also held it while a long press lasted).");
            }

            private void HatButtons(GContainer container, Source source, string name, GBinding binding, string where)
            {
                if (source.Kind != "hat")
                {
                    Result.NotImported.Add(where + ": hat buttons on an " + source.Kind + ".");
                    return;
                }
                int buttons = Int(container.Value("button-count", "button_count"), container.Sets.Count >= 8 ? 8 : 4);
                string[] roles = buttons >= 8
                    ? new[] { "up", "upright", "right", "downright", "down", "downleft", "left", "upleft" }
                    : new[] { "up", "right", "down", "left" };
                VirtualFunction function = Add(VirtualFunctionCatalog.HatButtons, name + " (hat buttons)", binding, where + " (hat buttons)");
                SetInput(function, "hat", source);
                function.Params["ways"] = buttons >= 8 ? 8 : 4;
                for (int i = 0; i < roles.Length && i < container.Sets.Count; i++)
                    ContainerOutput(function, roles[i], container.Sets[i], name + " " + roles[i], binding, where + " " + roles[i]);
            }

            // ---- merged axes

            private void ConvertMerge(GMerge merge)
            {
                string where = "Merged axis (mode " + merge.Mode + ") -> vJoy " + merge.VJoy.ToString(CultureInfo.InvariantCulture) + " axis " + merge.Axis.ToString(CultureInfo.InvariantCulture);
                if (merge.Axis < 1 || merge.Axis > 8 || merge.LowerAxis < 1 || merge.LowerAxis > 8 || merge.UpperAxis < 1 || merge.UpperAxis > 8)
                {
                    Result.NotImported.Add(where + ": an axis number outside 1..8.");
                    return;
                }
                GBinding binding = new GBinding { Mode = merge.Mode };
                VirtualFunction function = Add(VirtualFunctionCatalog.Merge, "Merged axis vJoy " + merge.VJoy.ToString(CultureInfo.InvariantCulture) + " " + VfTokens.AxisNames[merge.Axis - 1], binding, where);
                function.Inputs.Add(new VfInput { Role = "a", Device = Device(merge.LowerGuid, merge.LowerName), Input = AxisTokens[merge.LowerAxis - 1] });
                function.Inputs.Add(new VfInput { Role = "b", Device = Device(merge.UpperGuid, merge.UpperName), Input = AxisTokens[merge.UpperAxis - 1] });
                int mode;
                switch (merge.Operation.ToLowerInvariant())
                {
                    case "sum": mode = 1; break;
                    case "maximum": case "max": mode = 3; break;
                    case "minimum": case "min": mode = 4; break;
                    case "average": case "avg": mode = 2; break;
                    default:
                        mode = 2;
                        Result.Notes.Add(where + ": the operation \"" + merge.Operation + "\" was taken as average.");
                        break;
                }
                function.Params["mode"] = mode;
                function.Outputs.Add(new VfOutput { Role = "out", Target = AxisTokens[merge.Axis - 1], VJoyDevice = VJoy(merge.VJoy.ToString(CultureInfo.InvariantCulture)) });
            }
        }

        private struct Source
        {
            public string Device;
            public string Input;
            public string Kind;
        }

        // ---------------------------------------------------------------------------------------------- helpers

        private static string Where(GBinding binding)
        {
            string device = binding.DeviceName.Length > 0 ? binding.DeviceName : binding.DeviceGuid;
            string input = (binding.InputType == "axis" ? "axis " : binding.InputType == "hat" ? "hat " : "button ") + binding.InputId.ToString(CultureInfo.InvariantCulture);
            return device + " " + input + (binding.Mode.Length > 0 ? " (mode " + binding.Mode + ")" : string.Empty);
        }

        private static string Attr(XElement element, params string[] names)
        {
            foreach (string name in names)
            {
                XAttribute attribute = element.Attribute(name);
                if (attribute != null)
                    return attribute.Value.Trim();
            }
            return string.Empty;
        }

        private static string Text(XElement element)
        {
            return element == null ? string.Empty : element.Value.Trim();
        }

        private static string FirstText(XElement element, params string[] names)
        {
            foreach (string name in names)
            {
                XElement child = element.Element(name);
                if (child != null)
                    return child.Value.Trim();
                XAttribute attribute = element.Attribute(name);
                if (attribute != null)
                    return attribute.Value.Trim();
            }
            return string.Empty;
        }

        private static int Int(string text, int fallback)
        {
            int value;
            return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
