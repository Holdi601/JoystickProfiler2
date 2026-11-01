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
    // engine vjoy import-gremlin <file> --game <game> [--dry-run] [--replace] [--vjoy-device N] (docs/virtual-joystick.md
    // "Coming from Joystick Gremlin"): a Joystick Gremlin profile becomes virtual functions and modes of one game;
    // the report lists what was imported, what was approximated and what JoyPro cannot do.
    internal static partial class EngineVJoy
    {
        private static int ImportGremlinCommand(List<string> args, bool textOutput, out string payload)
        {
            string game = RequireGame(args);
            bool dryRun = PopFlag(args, "--dry-run");
            bool replace = PopFlag(args, "--replace");
            string deviceRaw = PopValue(args, "--vjoy-device");
            string file = PopValue(args, "--file");
            if (file == null && args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                file = args[0];
                args.RemoveAt(0);
            }
            RefuseLeftovers(args, "import-gremlin");
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("import-gremlin needs the Gremlin profile: engine vjoy import-gremlin <file.xml> --game <game>.");
            if (!File.Exists(file))
                throw new ArgumentException("The Gremlin profile was not found: " + file);
            VJoySettings settings = LoadSettings();
            int vjoyDevice = 0;
            if (deviceRaw != null && (!int.TryParse(deviceRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out vjoyDevice) || vjoyDevice < 1 || vjoyDevice > VJoySettings.MaxDevices))
                throw new ArgumentException("--vjoy-device takes 1..16.");
            // Without --vjoy-device the outputs go to the vJoy device JoyPro tunnels into (Settings), when one is set.
            if (vjoyDevice == 0 && VJoyTunnel.Instance > 0)
                vjoyDevice = VJoyTunnel.Instance;
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            DeviceScan scan = ScanDevices(null, caps);
            List<string> known = new List<string>();
            if (scan.Directory != null)
                known.AddRange(scan.Directory.Devices.Where(d => !(d.VendorId == VJoyVendorId && d.ProductId == VJoyProductId) && !IsVJoyName(d.InstanceName)).Select(d => d.JoyProName));
            known.AddRange(settings.Slots.Select(s => s.Source));
            known.AddRange(settings.AllFunctions().SelectMany(f => f.Value.Inputs).Where(i => i.Device.Length > 0 && !VfChain.IsChain(i)).Select(i => i.Device));
            known = known.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> usedIds = settings.AllFunctions().Where(f => !(replace && string.Equals(f.Key, game, StringComparison.OrdinalIgnoreCase))).Select(f => f.Value.Id).ToList();
            GremlinImportResult result = GremlinImporter.Import(File.ReadAllText(file), known, vjoyDevice, usedIds);

            List<VirtualFunction> list = settings.FunctionsOf(game);
            if (replace)
            {
                list.Clear();
                if (settings.Modes != null)
                    settings.Modes.Remove(game);
            }
            List<string> warnings = new List<string>();
            if (result.Modes.Count > 0)
            {
                VfModeSet set = settings.ModeSetOf(game);
                bool fresh = set.Modes.Count == 0;
                foreach (VfMode mode in result.Modes)
                {
                    VfMode existing = set.Find(mode.Name);
                    if (existing == null)
                        set.Modes.Add(mode.Clone());
                    else if (existing.Parent.Length == 0 && mode.Parent.Length > 0)
                        existing.Parent = mode.Parent;
                }
                if (fresh)
                    set.Start = result.StartMode;
                set.Normalize();
            }
            list.AddRange(result.Functions);
            string loop = VfChain.LoopProblem(list);
            if (loop.Length > 0)
                throw new ArgumentException(loop + " Nothing was imported.");
            if (!settings.IsAnyVirtual(game))
                warnings.Add(GameName(game) + " uses its physical devices: the imported functions run once it is set to Virtual (engine vjoy mode " + game + " virtual).");
            if (vjoyDevice > 0 && (vjoyDevice < settings.FirstDevice || vjoyDevice > settings.LastDevice))
                warnings.Add("vJoy " + vjoyDevice.ToString(CultureInfo.InvariantCulture) + " lies outside the vJoy devices JoyPro uses (" + settings.FirstDevice.ToString(CultureInfo.InvariantCulture) + "-" + settings.LastDevice.ToString(CultureInfo.InvariantCulture) + ").");
            VJoyPlan plan = BuildPlan(settings, caps, false, false);
            foreach (VirtualFunction function in result.Functions)
            {
                string label = VJoyPlanner.VJoyFunctionLabel(game, function, null);
                warnings.AddRange(plan.Warnings.Where(w => w.StartsWith(label, StringComparison.Ordinal)));
            }
            if (!dryRun)
                SaveSettings(settings);

            List<object> functions = result.Functions.Select(f => (object)new Dictionary<string, object>
            {
                { "Id", f.Id },
                { "Type", f.Type },
                { "TypeName", VirtualFunctionCatalog.Get(f.Type) == null ? f.Type : VirtualFunctionCatalog.Get(f.Type).Name },
                { "Name", f.Name },
                { "Modes", f.Modes },
                { "From", result.Origins.ContainsKey(f.Id) ? result.Origins[f.Id] : string.Empty },
                { "Inputs", f.Inputs.Where(i => i.Input.Length > 0).Select(i => i.Role + ": " + (VfChain.IsChain(i) ? VfChain.Describe(i, result.Functions) : i.Device + " " + i.Input)).ToList() },
                { "Outputs", f.Outputs.Select(o => o.Role + ": " + (o.Target.Length == 0 ? "feeds other functions" : (o.VJoyDevice > 0 ? "vJoy " + o.VJoyDevice.ToString(CultureInfo.InvariantCulture) + " " : string.Empty) + VfTokens.Describe(o.Target))).ToList() },
                { "Problems", FunctionProblems(settings, game, f) }
            }).ToList();
            string message = (dryRun ? "Dry run: would import " : "Imported ") + result.Functions.Count.ToString(CultureInfo.InvariantCulture) + " virtual function(s)" +
                             (result.Modes.Count > 0 ? " and " + result.Modes.Count.ToString(CultureInfo.InvariantCulture) + " mode(s)" : string.Empty) + " for " + GameName(game) + " from " + Path.GetFileName(file) +
                             " (" + result.Format + ")" + (replace ? ", replacing the game's functions and modes" : string.Empty) + "." +
                             (result.NotImported.Count > 0 ? " " + result.NotImported.Count.ToString(CultureInfo.InvariantCulture) + " thing(s) could not be imported - see the list." : string.Empty) +
                             (dryRun ? " Nothing was saved." : string.Empty);
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new
                {
                    Message = message,
                    result.Format,
                    Game = game,
                    DryRun = dryRun,
                    VJoyDevice = vjoyDevice,
                    result.VJoyDevices,
                    result.Devices,
                    Modes = result.Modes,
                    result.StartMode,
                    Functions = functions,
                    result.NotImported,
                    result.Notes,
                    Warnings = warnings.Distinct().ToList()
                });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(message);
            sb.AppendLine("Devices:");
            foreach (GremlinDeviceMatch device in result.Devices)
                sb.AppendLine("  " + device.GremlinName + " " + device.GremlinGuid + " -> " + device.JoyProDevice + " (" + (device.Match == "none" ? "not found" : "by " + device.Match) + ")");
            if (result.Modes.Count > 0)
            {
                sb.AppendLine("Modes:");
                foreach (VfMode mode in result.Modes)
                    sb.AppendLine("  " + mode.Name + (mode.Parent.Length > 0 ? " (inherits from " + mode.Parent + ")" : string.Empty) + (string.Equals(mode.Name, result.StartMode, StringComparison.OrdinalIgnoreCase) ? " [start]" : string.Empty));
            }
            sb.AppendLine("Functions:");
            foreach (VirtualFunction function in result.Functions)
                sb.AppendLine("  " + function.Id + " " + VirtualFunctionCatalog.Get(function.Type).Icon + " " + function.Name + (function.Modes.Count > 0 ? " [" + string.Join(", ", function.Modes) + "]" : string.Empty) + "  <- " + (result.Origins.ContainsKey(function.Id) ? result.Origins[function.Id] : string.Empty));
            if (result.NotImported.Count > 0)
            {
                sb.AppendLine("Not imported (JoyPro cannot do this):");
                foreach (string line in result.NotImported)
                    sb.AppendLine("  - " + line);
            }
            if (result.Notes.Count > 0)
            {
                sb.AppendLine("Imported, but different:");
                foreach (string line in result.Notes)
                    sb.AppendLine("  - " + line);
            }
            foreach (string warning in warnings.Distinct())
                sb.AppendLine("warning: " + warning);
            payload = sb.ToString();
            return 0;
        }
    }
}
