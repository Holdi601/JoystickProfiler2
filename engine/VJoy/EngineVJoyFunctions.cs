using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Games;
using JoyPro.Engine.VJoy;

namespace JoyPro.Engine
{
    // engine vjoy functions list|types|add|set|remove|test and engine vjoy limits (docs/virtual-joystick.md "Virtual
    // functions"). Functions live in the profile per game (VJoySettings.Functions); their vJoy outputs are reserved in
    // the table (VJoyPlanner), the forwarder evaluates them (forwarder-config writes them resolved).
    internal static partial class EngineVJoy
    {
        // The physical devices the virtual-mode games' functions read (HidHide hides them too).
        public static List<string> FunctionSources(VJoySettings settings)
        {
            return settings.AllFunctions()
                .Where(f => f.Value.Enabled && settings.IsAnyVirtual(f.Key))
                .SelectMany(f => f.Value.Inputs)
                .Where(i => i.Device.Length > 0 && i.Input.Length > 0)
                .Select(i => i.Device)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // A shift layer's unshifted output defaults to the input's own slot (vJoy slot, or the Xbox control of the
        // game's pad table), so the layer can take the input away from it while shifted.
        public static void FillShiftBase(VirtualFunction function, List<VJoySlot> slots, bool xbox)
        {
            if (function.Type != VirtualFunctionCatalog.Shift)
                return;
            foreach (VfInput input in function.Inputs.Where(i => i.Role.StartsWith("in", StringComparison.Ordinal) && i.Device.Length > 0))
            {
                string number = input.Role.Substring(2);
                VfOutput existing = function.OutputOf("base" + number);
                if (existing != null && existing.Target.Length > 0)
                    continue;
                VJoyInput physical;
                int direction;
                if (!VJoyInput.TryFromBindToken(input.Input, out physical, out direction))
                    continue;
                VJoySlot slot = slots.FirstOrDefault(s => string.Equals(s.Source, input.Device, StringComparison.OrdinalIgnoreCase) && string.Equals(VJoyPlanner.Normalize(s.Input), physical.ToSlotToken(), StringComparison.OrdinalIgnoreCase));
                if (slot == null)
                    continue;
                if (existing == null)
                {
                    existing = new VfOutput { Role = "base" + number };
                    function.Outputs.Add(existing);
                }
                existing.Target = xbox ? slot.Target.TrimEnd('~') : slot.Target;
                existing.VJoyDevice = xbox ? 0 : slot.Device;
            }
        }

        private static int FunctionsCommand(List<string> args, bool textOutput, out string payload)
        {
            string action = args.Count == 0 ? "list" : args[0].Trim().ToLowerInvariant();
            if (args.Count > 0)
                args.RemoveAt(0);
            switch (action)
            {
                case "list":
                    return FunctionsList(args, textOutput, out payload);
                case "types":
                    RefuseLeftovers(args, "functions types");
                    payload = textOutput ? TypesText() : EngineSerialization.ToJson(new { Types = VirtualFunctionCatalog.Types });
                    return 0;
                case "add":
                    return FunctionsAddOrSet(args, true, textOutput, out payload);
                case "set":
                    return FunctionsAddOrSet(args, false, textOutput, out payload);
                case "remove":
                    return FunctionsRemove(args, textOutput, out payload);
                case "test":
                    return FunctionsTest(args, textOutput, out payload);
                default:
                    payload = "Unknown vjoy functions action: " + action + ". Use list, types, add, set, remove or test.";
                    return 1;
            }
        }

        private static string TypesText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Virtual function types:");
            foreach (VfTypeInfo type in VirtualFunctionCatalog.Types)
            {
                sb.AppendLine("  " + type.Icon + " " + type.Id + " - " + type.Name + ": " + type.Summary);
                sb.AppendLine("      inputs: " + string.Join(", ", type.Inputs.Select(r => r.Role + (r.Numbered ? "<n>" : string.Empty) + " (" + r.Kind + (r.Optional ? ", optional" : string.Empty) + ")")));
                sb.AppendLine("      outputs: " + string.Join(", ", type.Outputs.Select(r => r.Role + (r.Numbered ? "<n>" : string.Empty) + " (" + r.Kind + (r.Optional ? ", optional" : string.Empty) + ")")));
                if (type.Params.Count > 0)
                    sb.AppendLine("      params: " + string.Join(", ", type.Params.Select(p => p.Key + "=" + p.Default.ToString(CultureInfo.InvariantCulture) + (p.Unit.Length > 0 ? " " + p.Unit : string.Empty))));
            }
            return sb.ToString();
        }

        private static string RequireGame(List<string> args)
        {
            string raw = PopValue(args, "--game");
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("--game <game> is needed.");
            string game = ResolveGame(raw);
            if (game.Length == 0)
                throw new ArgumentException("Unknown game: " + raw);
            return game;
        }

        // ---- list

        private static int FunctionsList(List<string> args, bool textOutput, out string payload)
        {
            string gameRaw = PopValue(args, "--game");
            RefuseLeftovers(args, "functions list");
            string game = string.Empty;
            if (!string.IsNullOrWhiteSpace(gameRaw))
            {
                game = ResolveGame(gameRaw);
                if (game.Length == 0)
                    throw new ArgumentException("Unknown game: " + gameRaw);
            }
            VJoySettings settings = LoadSettings();
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            VJoyPlan plan = BuildPlan(settings, caps, false, false);
            DeviceScan scan = ScanDevices(null, caps);
            List<Dictionary<string, object>> items = FunctionItems(settings, plan, caps, scan, game);
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new
                {
                    Game = game,
                    Functions = items,
                    InputDevices = InputDevices(scan),
                    Limits = game.Length > 0 ? LimitItem(game) : null,
                    Warnings = plan.Warnings
                });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Virtual functions" + (game.Length > 0 ? " of " + GameName(game) : string.Empty) + ": " + items.Count.ToString(CultureInfo.InvariantCulture));
            foreach (Dictionary<string, object> item in items)
            {
                VirtualFunction function = (VirtualFunction)item["Function"];
                sb.AppendLine("  " + function.Id + " | " + item["Game"] + " | " + item["Icon"] + " " + item["TypeName"] + " | " + function.Name + (function.Enabled ? string.Empty : " (off)"));
                foreach (VfInput input in function.Inputs.Where(i => i.Input.Length > 0))
                    sb.AppendLine("      in  " + input.Role + ": " + input.Device + " " + input.Input);
                foreach (Dictionary<string, object> output in (List<Dictionary<string, object>>)item["Outputs"])
                    sb.AppendLine("      out " + output["Role"] + ": " + output["Describe"] + (((string)output["Mode"]).Length > 0 ? " (" + output["Mode"] + ")" : string.Empty));
                foreach (string problem in (List<string>)item["Problems"])
                    sb.AppendLine("      problem: " + problem);
                foreach (string warning in (List<string>)item["Warnings"])
                    sb.AppendLine("      warning: " + warning);
            }
            payload = sb.ToString();
            return 0;
        }

        // The physical game controllers (not vJoy), for the editor's input pickers.
        internal static List<object> InputDevices(DeviceScan scan)
        {
            List<object> devices = new List<object>();
            if (scan == null || scan.Directory == null)
                return devices;
            foreach (GameDevice device in scan.Directory.Devices.Where(d => !(d.VendorId == VJoyVendorId && d.ProductId == VJoyProductId) && !IsVJoyName(d.InstanceName)))
            {
                List<string> ignored = new List<string>();
                scan.Directory.EnsureCapabilities(device, ignored);
                devices.Add(new Dictionary<string, object>
                {
                    { "Device", device.JoyProName },
                    { "Name", device.InstanceName },
                    { "Axes", device.CapabilitiesKnown ? device.AxisSlots.Select(a => VfTokens.AxisTokens[Math.Max(0, Math.Min(7, a))]).Distinct().ToList() : VfTokens.AxisTokens.ToList() },
                    { "Buttons", device.CapabilitiesKnown ? device.ButtonCount : 128 },
                    { "Hats", device.CapabilitiesKnown ? device.PovCount : 4 }
                });
            }
            return devices;
        }

        internal static List<Dictionary<string, object>> FunctionItems(VJoySettings settings, VJoyPlan plan, Dictionary<int, VJoyDeviceCaps> caps, DeviceScan scan, string onlyGame)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            foreach (KeyValuePair<string, VirtualFunction> entry in settings.AllFunctions())
            {
                if (onlyGame.Length > 0 && !string.Equals(entry.Key, onlyGame, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(FunctionItem(settings, plan, caps, scan, entry.Key, entry.Value));
            }
            return items;
        }

        internal static Dictionary<string, object> FunctionItem(VJoySettings settings, VJoyPlan plan, Dictionary<int, VJoyDeviceCaps> caps, DeviceScan scan, string game, VirtualFunction function)
        {
            VfTypeInfo type = VirtualFunctionCatalog.Get(function.Type);
            List<string> problems = VirtualFunctionCatalog.Validate(function);
            List<string> warnings = new List<string>();
            bool xbox = settings.IsXbox(game);
            if (!settings.IsAnyVirtual(game))
                warnings.Add(GameName(game) + " uses its physical devices: the function runs only while the game is in virtual mode (vJoy or Xbox pad).");
            foreach (VfInput input in function.Inputs.Where(i => i.Device.Length > 0 && i.Input.Length > 0))
            {
                if (scan != null && scan.Find(input.Device) == null)
                    warnings.Add(input.Role + ": " + input.Device + " is not connected now.");
            }
            List<Dictionary<string, object>> outputs = new List<Dictionary<string, object>>();
            List<KeyValuePair<int, string>> vjoyTargets = new List<KeyValuePair<int, string>>();
            foreach (VfOutput output in function.Outputs)
            {
                string kind;
                int index;
                int mask;
                bool parsed = output.Target.Length > 0 && VfTokens.TryParseOutput(output.Target, out kind, out index, out mask);
                bool vjoy = parsed && VfTokens.TryParseOutput(output.Target, out kind, out index, out mask) && (kind == "button" || kind == "axis" || kind == "hat");
                bool isXbox = parsed && output.Target.StartsWith("XB_", StringComparison.OrdinalIgnoreCase);
                int device = output.VJoyDevice <= 0 ? 1 : output.VJoyDevice;
                string bindDevice = string.Empty;
                if (vjoy)
                {
                    vjoyTargets.Add(new KeyValuePair<int, string>(device, output.Target));
                    bindDevice = scan == null ? string.Empty : scan.NameOf(device);
                    VJoyDeviceCaps deviceCaps;
                    VJoyInput target;
                    if (caps.TryGetValue(device, out deviceCaps) && deviceCaps.Configured && VJoyInput.TryParseSlotToken(output.Target, out target))
                    {
                        if (target.Kind == VJoyInputKind.Button && target.Index >= deviceCaps.Buttons)
                            warnings.Add(output.Role + ": vJoy device " + device.ToString(CultureInfo.InvariantCulture) + " has " + deviceCaps.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons, " + VfTokens.Describe(output.Target) +
                                         " needs more: \"Set up vJoy for maximum\" (128 buttons; Windows must restart afterwards).");
                        else if (target.Kind == VJoyInputKind.Pov && target.Index >= deviceCaps.Povs)
                            warnings.Add(output.Role + ": vJoy device " + device.ToString(CultureInfo.InvariantCulture) + " has " + deviceCaps.Povs.ToString(CultureInfo.InvariantCulture) + " hats, " + VfTokens.Describe(output.Target) + " needs \"Set up vJoy for maximum\".");
                        else if (target.Kind == VJoyInputKind.Axis && !deviceCaps.Axes.Contains(target.Index))
                            warnings.Add(output.Role + ": vJoy device " + device.ToString(CultureInfo.InvariantCulture) + " has no " + VfTokens.Describe(output.Target) + ": \"Set up vJoy for maximum\".");
                    }
                    if (xbox)
                        warnings.Add(output.Role + ": " + GameName(game) + " uses the virtual Xbox pad; a vJoy output does not reach it.");
                }
                else if (isXbox)
                {
                    bindDevice = XboxDeviceName;
                    if (settings.IsVirtual(game))
                        warnings.Add(output.Role + ": " + GameName(game) + " uses vJoy; an Xbox pad output does not reach it.");
                }
                VfRoleInfo role = VirtualFunctionCatalog.ExpandOutputs(function).FirstOrDefault(r => r.Role == output.Role);
                outputs.Add(new Dictionary<string, object>
                {
                    { "Role", output.Role },
                    { "Label", role == null ? output.Role : role.Label },
                    { "Target", output.Target },
                    { "VJoyDevice", vjoy ? device : 0 },
                    { "Mode", output.Mode ?? string.Empty },
                    { "Describe", output.Target.Length == 0 ? "none" : (vjoy ? "vJoy " + device.ToString(CultureInfo.InvariantCulture) + " " : string.Empty) + VfTokens.Describe(output.Target) },
                    { "BindDevice", bindDevice },
                    { "BindInput", output.Target.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase) ? string.Empty : output.Target }
                });
            }
            warnings.AddRange(GameInputLimits.Check(game, GameName(game), vjoyTargets, "this function's outputs on"));
            foreach (string warning in plan.Warnings.Where(w => w.StartsWith(VJoyPlanner.VJoyFunctionLabel(game, function, null), StringComparison.Ordinal)))
                warnings.Add(warning);
            return new Dictionary<string, object>
            {
                { "Game", game },
                { "GameName", GameName(game) },
                { "Id", function.Id },
                { "Type", function.Type },
                { "TypeName", type == null ? function.Type : type.Name },
                { "Icon", type == null ? "?" : type.Icon },
                { "Summary", type == null ? string.Empty : type.Summary },
                { "Function", function },
                { "Outputs", outputs },
                { "Problems", problems },
                { "Warnings", warnings.Distinct().ToList() }
            };
        }

        // ---- add / set

        private static int FunctionsAddOrSet(List<string> args, bool add, bool textOutput, out string payload)
        {
            string game = RequireGame(args);
            string id = PopValue(args, "--id");
            string type = PopValue(args, "--type");
            string name = PopValue(args, "--name");
            string spec = PopValue(args, "--spec");
            List<string> inputs = PopValues(args, "--input");
            List<string> outputs = PopValues(args, "--output");
            List<string> parameters = PopValues(args, "--param");
            string edges = PopValue(args, "--edges");
            string count = PopValue(args, "--count");
            string tuning = PopValue(args, "--tuning");
            bool? keyboard = ParseOnOff(PopValue(args, "--keyboard"), "--keyboard");
            bool? enabled = ParseOnOff(PopValue(args, "--enabled"), "--enabled");
            bool autoOutputs = add ? !PopFlag(args, "--no-auto-outputs") : PopFlag(args, "--auto-outputs");
            bool dryRun = PopFlag(args, "--dry-run");
            RefuseLeftovers(args, add ? "functions add" : "functions set");

            VJoySettings settings = LoadSettings();
            List<VirtualFunction> list = settings.FunctionsOf(game);
            VirtualFunction function;
            VirtualFunction existing = null;
            if (add)
            {
                if (spec != null)
                {
                    function = ParseSpec(spec);
                    if (!string.IsNullOrWhiteSpace(type))
                        function.Type = type;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(type))
                        throw new ArgumentException("functions add needs --type <type> (engine vjoy functions types) or --spec <json|@file>.");
                    function = VirtualFunctionCatalog.Create(type, name);
                }
                function.Normalize();
                if (VirtualFunctionCatalog.Get(function.Type) == null)
                    throw new ArgumentException("Unknown function type: " + function.Type + ". Types: " + string.Join(", ", VirtualFunctionCatalog.Types.Select(t => t.Id)));
                function.Id = NextFunctionId(settings);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("functions set needs --id <function id>.");
                existing = list.FirstOrDefault(f => string.Equals(f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    throw new ArgumentException("No function " + id + " for " + GameName(game) + " (engine vjoy functions list --game " + game + ").");
                function = spec != null ? ParseSpec(spec) : existing.Clone();
                function.Id = existing.Id;
                if (!string.IsNullOrWhiteSpace(type))
                    function.Type = type;
                function.Normalize();
                if (VirtualFunctionCatalog.Get(function.Type) == null)
                    throw new ArgumentException("Unknown function type: " + function.Type + ".");
            }
            if (!string.IsNullOrWhiteSpace(name))
                function.Name = name.Trim();
            if (string.IsNullOrWhiteSpace(function.Name))
                function.Name = VirtualFunctionCatalog.Get(function.Type).Name;
            if (keyboard.HasValue)
                function.Keyboard = keyboard.Value;
            if (enabled.HasValue)
                function.Enabled = enabled.Value;
            if (tuning != null)
                function.Tuning = tuning;
            if (edges != null)
                function.Edges = ParseNumbers(edges, "--edges");
            if (count != null)
            {
                int n;
                if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1 || n > 32)
                    throw new ArgumentException("--count takes 1..32.");
                SetCount(function, n);
            }
            SyncNumberedRoles(function);
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            DeviceScan scan = ScanDevices(null, caps);
            foreach (string value in parameters)
            {
                int eq = value.IndexOf('=');
                double number;
                if (eq <= 0 || !double.TryParse(value.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new ArgumentException("--param takes key=number, e.g. --param longMs=600.");
                function.Params[value.Substring(0, eq).Trim()] = number;
            }
            foreach (string value in inputs)
                ApplyInput(function, value, scan);
            foreach (string value in outputs)
                ApplyOutput(function, value);
            List<string> notes = new List<string>();
            if (autoOutputs)
                notes.AddRange(AllocateOutputs(settings, game, function, caps));

            if (add)
                list.Add(function);
            else
                list[list.IndexOf(existing)] = function;
            VJoyPlan plan = BuildPlan(settings, caps, false, false);
            if (!dryRun)
                SaveSettings(settings);
            Dictionary<string, object> item = FunctionItem(settings, plan, caps, scan, game, function);
            string message = (dryRun ? "Dry run: " : string.Empty) + (add ? "Added" : "Updated") + " the function " + function.Id + " \"" + function.Name + "\" (" + item["TypeName"] + ") for " + GameName(game) + "." +
                             (dryRun ? " Nothing was saved." : string.Empty);
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Message = message, Item = item, Notes = notes });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(message);
            foreach (Dictionary<string, object> output in (List<Dictionary<string, object>>)item["Outputs"])
                sb.AppendLine("  out " + output["Role"] + ": " + output["Describe"]);
            foreach (string note in notes)
                sb.AppendLine("  note: " + note);
            foreach (string problem in (List<string>)item["Problems"])
                sb.AppendLine("  problem: " + problem);
            foreach (string warning in (List<string>)item["Warnings"])
                sb.AppendLine("  warning: " + warning);
            payload = sb.ToString();
            return 0;
        }

        private static VirtualFunction ParseSpec(string spec)
        {
            string json = spec.Trim();
            if (json.StartsWith("@", StringComparison.Ordinal))
            {
                string path = json.Substring(1);
                if (!File.Exists(path))
                    throw new ArgumentException("Spec file not found: " + path);
                json = File.ReadAllText(path);
            }
            try
            {
                VirtualFunction function = EngineSerialization.FromJson<VirtualFunction>(json);
                if (function == null)
                    throw new ArgumentException("The spec is empty.");
                function.Normalize();
                return function;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                throw new ArgumentException("The function spec is not valid JSON for a function: " + ex.Message);
            }
        }

        private static List<double> ParseNumbers(string text, string option)
        {
            List<double> values = new List<double>();
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(option + " takes numbers, e.g. 0.1,0.9.");
                values.Add(value);
            }
            return values;
        }

        public static string NextFunctionId(VJoySettings settings)
        {
            HashSet<string> used = new HashSet<string>(settings.AllFunctions().Select(f => f.Value.Id), StringComparer.OrdinalIgnoreCase);
            for (int i = 1; ; i++)
            {
                string id = "f" + i.ToString(CultureInfo.InvariantCulture);
                if (!used.Contains(id))
                    return id;
            }
        }

        // Numbered roles (cycle steps, shift entries, macro outputs): n of them. Bands follow their edges.
        public static void SetCount(VirtualFunction function, int n)
        {
            VfTypeInfo info = VirtualFunctionCatalog.Get(function.Type);
            if (info == null)
                return;
            if (info.Id == VirtualFunctionCatalog.Bands)
            {
                // n bands: n - 1 edges spread evenly unless the edges already fit.
                if (function.Edges.Count != n - 1)
                    function.Edges = Enumerable.Range(1, Math.Max(0, n - 1)).Select(i => Math.Round(i / (double)n, 3)).ToList();
                return;
            }
            foreach (VfRoleInfo role in info.Inputs.Where(r => r.Numbered))
            {
                function.Inputs.RemoveAll(i => NumberOf(i.Role, role.Role) > n);
                for (int i = 1; i <= n; i++)
                {
                    string name = role.Role + i.ToString(CultureInfo.InvariantCulture);
                    if (function.InputOf(name) == null)
                        function.Inputs.Add(new VfInput { Role = name });
                }
            }
            foreach (VfRoleInfo role in info.Outputs.Where(r => r.Numbered))
            {
                function.Outputs.RemoveAll(o => NumberOf(o.Role, role.Role) > n);
                if (role.Optional && info.Id == VirtualFunctionCatalog.Shift)
                    continue;
                for (int i = 1; i <= n; i++)
                {
                    string name = role.Role + i.ToString(CultureInfo.InvariantCulture);
                    if (function.OutputOf(name) == null)
                        function.Outputs.Add(new VfOutput { Role = name, Mode = role.HasMode ? VirtualFunctionCatalog.ModeHold : string.Empty });
                }
            }
        }

        // Bands: one output role per band (band1 .. band<edges+1>).
        public static void SyncNumberedRoles(VirtualFunction function)
        {
            if (function.Type != VirtualFunctionCatalog.Bands)
                return;
            int bands = function.Edges.Count + 1;
            function.Outputs.RemoveAll(o => NumberOf(o.Role, "band") > bands);
            for (int i = 1; i <= bands; i++)
            {
                string name = "band" + i.ToString(CultureInfo.InvariantCulture);
                if (function.OutputOf(name) == null)
                    function.Outputs.Add(new VfOutput { Role = name, Mode = VirtualFunctionCatalog.ModeHold });
            }
            function.Outputs = function.Outputs.OrderBy(o => NumberOf(o.Role, "band")).ToList();
        }

        private static int NumberOf(string role, string prefix)
        {
            int number;
            return role != null && role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(role.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        // --input role=<device>|<token>: the device by its JoyPro name ("Name {GUID}") or a unique part of the name.
        private static void ApplyInput(VirtualFunction function, string value, DeviceScan scan)
        {
            int eq = value.IndexOf('=');
            int bar = value.LastIndexOf('|');
            if (eq <= 0)
                throw new ArgumentException("--input takes role=<device>|<input>, e.g. --input \"in=VPC Throttle {GUID}|JOY_Z\" (or role= to clear).");
            string role = value.Substring(0, eq).Trim().ToLowerInvariant();
            VfInput input = function.InputOf(role);
            if (input == null)
            {
                input = new VfInput { Role = role };
                function.Inputs.Add(input);
            }
            string rest = value.Substring(eq + 1).Trim();
            if (rest.Length == 0)
            {
                input.Device = input.Input = string.Empty;
                return;
            }
            if (bar <= eq)
                throw new ArgumentException("--input takes role=<device>|<input>, e.g. --input \"in=VPC Throttle {GUID}|JOY_Z\".");
            string device = value.Substring(eq + 1, bar - eq - 1).Trim();
            string token = value.Substring(bar + 1).Trim().ToUpperInvariant();
            string kind;
            int index;
            int direction;
            if (!VfTokens.TryParseInput(token, out kind, out index, out direction))
                throw new ArgumentException(token + " is not a joystick input (JOY_X .. JOY_SLIDER2, JOY_BTN<n>, POV<n>, JOY_BTN_POV<n>_<U|UR|R|DR|D|DL|L|UL>).");
            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(device, out name, out guid) && scan != null && scan.Directory != null)
            {
                List<GameDevice> matches = scan.Directory.Devices.Where(d => d.JoyProName.IndexOf(device, StringComparison.OrdinalIgnoreCase) >= 0 && !IsVJoyName(d.InstanceName)).ToList();
                if (matches.Count == 1)
                    device = matches[0].JoyProName;
                else
                    throw new ArgumentException(matches.Count == 0 ? "No connected device matches \"" + device + "\"." : "\"" + device + "\" matches several devices: " + string.Join("; ", matches.Select(m => m.JoyProName)) + ".");
            }
            input.Device = device;
            input.Input = token;
        }

        // --output role=<token>[@<vJoy device>][:<mode>] (role= clears it, role=auto lets JoyPro pick).
        private static void ApplyOutput(VirtualFunction function, string value)
        {
            int eq = value.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException("--output takes role=<target>[@<vJoy device>][:<hold|enter|leave|both>], e.g. --output band1=JOY_BTN30@1:enter.");
            string role = value.Substring(0, eq).Trim().ToLowerInvariant();
            string rest = value.Substring(eq + 1).Trim();
            VfOutput output = function.OutputOf(role);
            if (output == null)
            {
                output = new VfOutput { Role = role };
                function.Outputs.Add(output);
            }
            int colon = rest.IndexOf(':');
            if (colon >= 0)
            {
                output.Mode = rest.Substring(colon + 1).Trim().ToLowerInvariant();
                rest = rest.Substring(0, colon).Trim();
            }
            int at = rest.IndexOf('@');
            int device = 0;
            if (at >= 0)
            {
                if (!int.TryParse(rest.Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out device) || device < 1 || device > VJoySettings.MaxDevices)
                    throw new ArgumentException("The vJoy device after @ is 1..16.");
                rest = rest.Substring(0, at).Trim();
            }
            string token = rest.ToUpperInvariant();
            if (token.Length == 0 || token == "AUTO")
            {
                output.Target = string.Empty;
                output.VJoyDevice = device;
                return;
            }
            string kind;
            int index;
            int mask;
            if (!VfTokens.TryParseOutput(token, out kind, out index, out mask))
                throw new ArgumentException(token + " is not a virtual output (JOY_BTN<n>, JOY_X .. JOY_SLIDER2, POV<n>, XB_A .., KEY_<key>).");
            output.Target = token;
            output.VJoyDevice = kind == "button" || kind == "axis" || kind == "hat" ? (device > 0 ? device : (output.VJoyDevice > 0 ? output.VJoyDevice : 1)) : 0;
        }

        // Gives every output role without a target a free virtual output: on vJoy the lowest free button/axis/hat
        // (vJoy's maximum of 128 buttons, 8 axes, 4 hats is assumed; what the device or the game lacks is warned about),
        // on the Xbox pad a free control. The table's slots and every other function's outputs are taken.
        public static List<string> AllocateOutputs(VJoySettings settings, string game, VirtualFunction function, Dictionary<int, VJoyDeviceCaps> caps)
        {
            List<string> notes = new List<string>();
            bool xbox = settings.IsXbox(game);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> xboxTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VJoySlot slot in settings.Slots)
                taken.Add(slot.Device.ToString(CultureInfo.InvariantCulture) + ":" + VJoyPlanner.Normalize(slot.Target));
            foreach (VJoySlot slot in settings.XboxTable(game))
                xboxTaken.Add(slot.Target.TrimEnd('~').ToUpperInvariant());
            foreach (KeyValuePair<string, VirtualFunction> other in settings.AllFunctions())
            {
                foreach (VfOutput output in other.Value.Outputs.Where(o => o.Target.Length > 0))
                {
                    if (output.Target.StartsWith("XB_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(other.Key, game, StringComparison.OrdinalIgnoreCase))
                            xboxTaken.Add(output.Target);
                    }
                    else
                        taken.Add((output.VJoyDevice <= 0 ? 1 : output.VJoyDevice).ToString(CultureInfo.InvariantCulture) + ":" + VJoyPlanner.Normalize(output.Target));
                }
            }
            foreach (VfOutput output in function.Outputs.Where(o => o.Target.Length > 0))
            {
                if (output.Target.StartsWith("XB_", StringComparison.OrdinalIgnoreCase))
                    xboxTaken.Add(output.Target);
                else
                    taken.Add((output.VJoyDevice <= 0 ? 1 : output.VJoyDevice).ToString(CultureInfo.InvariantCulture) + ":" + VJoyPlanner.Normalize(output.Target));
            }
            Dictionary<int, string> avoid = AvoidAxes(settings);
            GameInputLimit limit = GameInputLimits.For(game);
            List<VfRoleInfo> roles = VirtualFunctionCatalog.ExpandOutputs(function);
            foreach (VfOutput output in function.Outputs.Where(o => o.Target.Length == 0).ToList())
            {
                VfRoleInfo role = roles.FirstOrDefault(r => r.Role == output.Role);
                if (role == null || (function.Type == VirtualFunctionCatalog.Shift && output.Role.StartsWith("base", StringComparison.Ordinal)))
                    continue;
                string want = role.Kind;
                if (function.Type == VirtualFunctionCatalog.Shift)
                {
                    VfInput source = function.InputOf("in" + output.Role.Substring(3));
                    string sourceKind;
                    int sourceIndex;
                    int sourceDirection;
                    want = source != null && VfTokens.TryParseInput(source.Input, out sourceKind, out sourceIndex, out sourceDirection) ? (sourceKind == "hatdir" ? "button" : sourceKind) : "button";
                }
                if (want == "any")
                    want = "button";
                if (xbox)
                {
                    IEnumerable<string> pool = want == "axis" ? XboxControls.Axes : want == "hat" ? new[] { XboxControls.DPad } : XboxControls.FreeButtons;
                    string free = pool.FirstOrDefault(c => !xboxTaken.Contains(c) && !(want == "hat" && XboxControls.FreeButtons.Where(b => b.StartsWith("XB_DPAD_", StringComparison.Ordinal)).Any(xboxTaken.Contains)));
                    if (free == null)
                    {
                        notes.Add(output.Role + ": no free Xbox pad control left for " + VirtualFunctionCatalog.KindWord(want) + ".");
                        continue;
                    }
                    xboxTaken.Add(free);
                    output.Target = free;
                    output.VJoyDevice = 0;
                    continue;
                }
                bool found = false;
                List<int> devices = Enumerable.Range(settings.FirstDevice, settings.LastDevice - settings.FirstDevice + 1).ToList();
                if (output.VJoyDevice > 0)
                {
                    devices.Remove(output.VJoyDevice);
                    devices.Insert(0, output.VJoyDevice);
                }
                foreach (int device in devices)
                {
                    int max = want == "axis" ? VJoySettings.MaxAxes : want == "hat" ? VJoySettings.MaxPovs : VJoySettings.MaxButtons;
                    for (int i = 0; i < max && !found; i++)
                    {
                        if (want == "axis" && avoid.ContainsKey(i))
                            continue;
                        string token = want == "axis" ? VfTokens.AxisTokens[i] : want == "hat" ? VfTokens.HatToken(i) : VfTokens.ButtonToken(i);
                        string key = device.ToString(CultureInfo.InvariantCulture) + ":" + VJoyPlanner.Normalize(token);
                        if (taken.Contains(key))
                            continue;
                        taken.Add(key);
                        output.Target = token;
                        output.VJoyDevice = device;
                        found = true;
                        VJoyDeviceCaps deviceCaps;
                        if (want == "button" && caps.TryGetValue(device, out deviceCaps) && deviceCaps.Configured && i >= deviceCaps.Buttons)
                            notes.Add(output.Role + ": " + VfTokens.Describe(token) + " on vJoy " + device.ToString(CultureInfo.InvariantCulture) + " is above the " + deviceCaps.Buttons.ToString(CultureInfo.InvariantCulture) +
                                      " buttons the device has now: \"Set up vJoy for maximum\" (Windows must restart afterwards).");
                        if (want == "button" && limit.Known && limit.Buttons >= 0 && i >= limit.Buttons)
                            notes.Add(output.Role + ": " + GameName(game) + " reads only the first " + limit.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons of a joystick: " + VfTokens.Describe(token) + " will not work there.");
                    }
                    if (found)
                        break;
                }
                if (!found)
                    notes.Add(output.Role + ": no free vJoy " + (want == "axis" ? "axis" : want == "hat" ? "hat" : "button") + " left on devices " +
                              settings.FirstDevice.ToString(CultureInfo.InvariantCulture) + "-" + settings.LastDevice.ToString(CultureInfo.InvariantCulture) + ".");
            }
            return notes;
        }

        // ---- remove

        private static int FunctionsRemove(List<string> args, bool textOutput, out string payload)
        {
            string game = RequireGame(args);
            string id = PopValue(args, "--id");
            bool dryRun = PopFlag(args, "--dry-run");
            RefuseLeftovers(args, "functions remove");
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("functions remove needs --id <function id>.");
            VJoySettings settings = LoadSettings();
            List<VirtualFunction> list = settings.FunctionsOf(game);
            VirtualFunction function = list.FirstOrDefault(f => string.Equals(f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (function == null)
                throw new ArgumentException("No function " + id + " for " + GameName(game) + ".");
            list.Remove(function);
            if (!dryRun)
                SaveSettings(settings);
            string message = (dryRun ? "Dry run: would remove" : "Removed") + " the function " + function.Id + " \"" + function.Name + "\" of " + GameName(game) + "; its virtual outputs are free again.";
            payload = textOutput ? message : EngineSerialization.ToJson(new { Message = message, Id = function.Id, Game = game });
            return 0;
        }

        // ---- test

        private static int FunctionsTest(List<string> args, bool textOutput, out string payload)
        {
            string gameRaw = PopValue(args, "--game");
            string id = PopValue(args, "--id");
            string spec = PopValue(args, "--spec");
            string streamText = PopValue(args, "--stream");
            string rateText = PopValue(args, "--rate");
            string durationText = PopValue(args, "--duration");
            RefuseLeftovers(args, "functions test");
            VirtualFunction function;
            if (spec != null)
                function = ParseSpec(spec);
            else
            {
                if (string.IsNullOrWhiteSpace(gameRaw) || string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("functions test needs --game <game> --id <function id>, or --spec <json|@file>.");
                string game = ResolveGame(gameRaw);
                if (game.Length == 0)
                    throw new ArgumentException("Unknown game: " + gameRaw);
                VJoySettings settings = LoadSettings();
                function = settings.FunctionsOf(game).FirstOrDefault(f => string.Equals(f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (function == null)
                    throw new ArgumentException("No function " + id + " for " + GameName(game) + ".");
                function = function.Clone();
                FillShiftBase(function, settings.IsXbox(game) ? settings.XboxTable(game) : settings.Slots, settings.IsXbox(game));
            }
            double rate = 240;
            if (rateText != null && (!double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) || rate < 10 || rate > 2000))
                throw new ArgumentException("--rate takes 10..2000 Hz.");
            double defaultDuration;
            string defaultStream = VfSimulator.DefaultStream(function, out defaultDuration);
            double duration = defaultDuration;
            if (durationText != null && (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || duration <= 0 || duration > 600000))
                throw new ArgumentException("--duration takes milliseconds (1..600000).");
            string streamUsed = streamText ?? defaultStream;
            VfSimulator simulator = new VfSimulator(function);
            List<VfEvent> events = simulator.Run(VfSimulator.ParseStream(streamUsed), rate, duration);
            List<string> problems = VirtualFunctionCatalog.Validate(function).Where(p => !p.EndsWith("no input chosen.", StringComparison.Ordinal) && !p.EndsWith("no virtual output.", StringComparison.Ordinal)).ToList();
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Function = function.Id, function.Type, Rate = rate, Duration = duration, Stream = streamUsed, Events = events, Keys = simulator.KeyLog, Problems = problems });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Simulated " + (function.Name.Length > 0 ? function.Name : function.Type) + " at " + rate.ToString(CultureInfo.InvariantCulture) + " Hz for " + duration.ToString(CultureInfo.InvariantCulture) + " ms (nothing goes to vJoy, the pad or the keyboard).");
            sb.AppendLine("Input stream: " + streamUsed);
            foreach (VfEvent e in events)
                sb.AppendLine("  " + e.TimeMs.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8) + " ms  " + e.Output + " (" + e.Target + ") -> " + e.Value);
            foreach (string key in simulator.KeyLog)
                sb.AppendLine("  " + key + " (simulated, not sent)");
            foreach (string problem in problems)
                sb.AppendLine("  problem: " + problem);
            payload = sb.ToString();
            return 0;
        }

        // ---- engine vjoy limits [--game g]

        internal static Dictionary<string, object> LimitItem(string game)
        {
            GameInputLimit limit = GameInputLimits.For(game);
            return new Dictionary<string, object>
            {
                { "Game", game },
                { "Name", GameName(game) },
                { "Known", limit.Known },
                { "Buttons", limit.Buttons },
                { "Axes", limit.Axes },
                { "Hats", limit.Hats },
                { "FourWayHats", limit.FourWayHats },
                { "Interface", limit.Interface },
                { "Confidence", limit.Known ? limit.Confidence : "unknown" },
                { "Describe", limit.Describe() },
                { "Note", limit.Note },
                { "Source", limit.Source }
            };
        }

        private static int LimitsCommand(List<string> args, bool textOutput, out string payload)
        {
            string gameRaw = PopValue(args, "--game");
            RefuseLeftovers(args, "limits");
            List<string> games = string.IsNullOrWhiteSpace(gameRaw) ? GameIds() : new List<string> { ResolveGame(gameRaw) };
            if (games.Any(g => g.Length == 0))
                throw new ArgumentException("Unknown game: " + gameRaw);
            List<Dictionary<string, object>> items = games.Select(LimitItem).ToList();
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { VJoyMaximum = new { Buttons = VJoySettings.MaxButtons, Axes = VJoySettings.MaxAxes, Hats = VJoySettings.MaxPovs, Devices = VJoySettings.MaxDevices }, Games = items });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("vJoy offers up to 128 buttons, 8 axes and 4 hats per device (16 devices). What the games read of one joystick:");
            foreach (Dictionary<string, object> item in items)
                sb.AppendLine("  " + item["Game"] + ": " + item["Describe"] + (((string)item["Note"]).Length > 0 ? " - " + item["Note"] : string.Empty));
            payload = sb.ToString();
            return 0;
        }
    }
}
