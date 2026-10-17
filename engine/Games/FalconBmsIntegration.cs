using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using JoyPro.Engine.Axis;
using Microsoft.Win32;

namespace JoyPro.Engine.Games
{
    // Falcon BMS 4.36+: the launcher keeps one device file per controller (User\Config\Setup.v100.<product>
    // {<DirectInput instance GUID>}.xml) and rebuilds the key file, axismapping.dat, joystick.cal and DeviceSorting.txt
    // from them on every Launch. JoyPro edits only these device files: the press slots of buttons (<dx>), hat directions
    // (<pov>) and the axis names (<axis>) of cataloged callbacks and flight axes. BMS's shift layer is a JoyPro modifier
    // on the button that holds SimHotasPinkyShift. See docs/games/falcon-bms.md.
    internal sealed class FalconBmsIntegration : IGameIntegration, IGameOwnershipTracking, IGameSettingsExport, IGameSettingsFile
    {
        public string GameId { get { return "FalconBMS"; } }
        public string DisplayName { get { return "Falcon BMS"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "bms", "falconbms", "falcon" }; } }
        public string DbFolder { get { return "FalconBMS"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes { get { return "Experimental. JoyPro writes the BMS launcher's device files in User\\Config; the launcher builds the key file and the axis setup from them on its next Launch. Close BMS and its launcher before exporting. A connected device without a device file gets one (the launcher's stock file of that model, else an empty one)."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "Falcon BMS", "FalconBMS_Alternative_Launcher" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // The key file stays BMS's; modifiers: only the one DX shift layer.
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }

        public const string FullKeyFile = "BMS - Full.key";
        private const string Nothing = "SimDoNothing";
        private const string ShiftCallback = "SimHotasPinkyShift";
        private const string AxisPrefix = "axis/";
        private const string UnsetDate = "1998-12-12T12:00:00";
        private const int PressSlots = 2;
        private static readonly Regex DeviceFileName = new Regex(@"^Setup\.v100\.(?<name>.*) \{(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}\.xml$", RegexOptions.Compiled);

        // Key-file sections with cockpit and flight callbacks; UI, 3rd party, simulation/hardware, Winamp, development,
        // views (7.x) and radio menus (8.x) stay out.
        private static readonly Regex FlightSection = new Regex(@"^=+\s*(?<num>[2-5]\.\d\d|6\.0[1-5]|6\.09)\s+(?<title>.+?)\s*=+$", RegexOptions.Compiled);

        // The launcher's axis functions (AxisName values) except the view axes FOV and Camera_Distance.
        private static readonly string[] AxisNames =
        {
            "Roll", "Pitch", "Yaw", "Throttle", "Throttle_Right", "Toe_Brake", "Toe_Brake_Right", "Trim_Roll", "Trim_Pitch",
            "Trim_Yaw", "Radar_Antenna_Elevation", "Cursor_X", "Cursor_Y", "Range_Knob", "HUD_Brightness", "Reticle_Depression",
            "HMS_Brightness", "FLIR_Brightness", "Intercom", "COMM_Channel_1", "COMM_Channel_2", "MSL_Volume", "Threat_Volume",
            "AI_vs_IVC", "HSI_Course_Knob", "HSI_Heading_Knob", "Altimeter_Knob", "ILS_Volume_Knob"
        };

        // Launcher steps (BmsAxisModel): dead zone 1/5/10 %, saturation 95/90/85 % of the half travel.
        private static readonly string[] Steps = BmsAxisModel.StepNames;

        private sealed class DeviceFile
        {
            public string Path;
            public string Name;
            public Guid Guid;
            public XDocument Document;
            public bool HasBom;
            public bool CrLf;
            public bool Changed;
            public XElement Axes;
            // The top-level lists, plus the profileDefaultF16 copies older launchers keep; the last non-empty one is read.
            public List<XElement> DxLists = new List<XElement>();
            public List<XElement> PovLists = new List<XElement>();
        }

        // The shift button: SimHotasPinkyShift in the press slot of a button.
        private sealed class ShiftButton
        {
            public DeviceFile File;
            public int Button;
        }

        private sealed class Target
        {
            public DeviceFile File;
            public string Kind;
            public int Index;
            public int Direction;
            public int Slot;
            public string ActionId;
            public string Value;
            public GameInputBinding Binding;

            public string Key
            {
                get
                {
                    if (Kind == "AX")
                        return "AX:" + Index.ToString(CultureInfo.InvariantCulture);
                    if (Kind == "DX")
                        return "DX:" + Index.ToString(CultureInfo.InvariantCulture) + ":" + Slot.ToString(CultureInfo.InvariantCulture);
                    return "POV:" + Index.ToString(CultureInfo.InvariantCulture) + ":" + Direction.ToString(CultureInfo.InvariantCulture) + ":" + Slot.ToString(CultureInfo.InvariantCulture);
                }
            }

            public string OwnerToken
            {
                get { return File.Guid.ToString("D").ToUpperInvariant() + "|" + Key; }
            }
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            return DiscoverInstallRoots()
                .Select(r => System.IO.Path.Combine(r, "User", "Config"))
                .Where(Directory.Exists)
                .ToList();
        }

        // Newest version first: the registry entries of the installer, then "Falcon BMS*" folders on fixed drives.
        public static List<string> DiscoverInstallRoots()
        {
            return GameInstalls.OverrideOr("FalconBMS", DetectInstallRoots);
        }

        private static List<string> DetectInstallRoots()
        {
            List<string> roots = new List<string>();
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey sims = baseKey.OpenSubKey(@"SOFTWARE\Benchmark Sims"))
                    {
                        if (sims == null)
                            continue;
                        foreach (string name in sims.GetSubKeyNames())
                        {
                            using (RegistryKey install = sims.OpenSubKey(name))
                            {
                                string dir = install == null ? null : install.GetValue("baseDir") as string;
                                if (!string.IsNullOrWhiteSpace(dir))
                                    roots.Add(dir.Trim().TrimEnd('\\'));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.AddRange(Directory.GetDirectories(drive.RootDirectory.FullName, "Falcon BMS*"));
                }
                catch (Exception)
                {
                }
            }
            return roots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(r => System.IO.Path.GetFileName(r), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string FindFullKeyFile()
        {
            return DiscoverInstallRoots()
                .Select(r => System.IO.Path.Combine(r, "User", "Config", FullKeyFile))
                .FirstOrDefault(File.Exists);
        }

        // Product version of Falcon BMS.exe (e.g. 4.38.1) for the DB update check; empty when unknown.
        public static string ReadVersion(string installRoot)
        {
            string exe = System.IO.Path.Combine(installRoot, "Bin", "x64", "Falcon BMS.exe");
            if (!File.Exists(exe))
                return string.Empty;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
            return (info.ProductVersion ?? info.FileVersion ?? string.Empty).Trim();
        }

        // Ownership lives in the config folder, next to the device files.
        private static string OwnershipBase(string configPath)
        {
            return System.IO.Path.Combine(configPath, "JoyPro");
        }

        private static List<DeviceFile> LoadDeviceFiles(string configPath, List<string> errors, bool requireAny = true)
        {
            List<DeviceFile> devices = new List<DeviceFile>();
            if (!Directory.Exists(configPath))
            {
                errors.Add("BMS config folder not found: " + configPath + " (pass the User\\Config folder of the BMS install).");
                return devices;
            }

            foreach (string file in Directory.GetFiles(configPath, "Setup.v100.*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Match name = DeviceFileName.Match(System.IO.Path.GetFileName(file));
                Guid guid;
                if (!name.Success || !Guid.TryParse(name.Groups["guid"].Value, out guid))
                    continue;

                byte[] bytes = File.ReadAllBytes(file);
                string text = Encoding.UTF8.GetString(bytes);
                XDocument document;
                try
                {
                    document = XDocument.Parse(text.TrimStart('﻿'), LoadOptions.PreserveWhitespace);
                }
                catch (XmlException ex)
                {
                    errors.Add("Unreadable BMS device file '" + file + "': " + ex.Message);
                    continue;
                }
                XElement root = document.Root;
                if (root == null || root.Name.LocalName != "JoyAssgn")
                    continue;

                DeviceFile device = new DeviceFile
                {
                    Path = file,
                    Name = name.Groups["name"].Value.Trim(),
                    Guid = guid,
                    Document = document,
                    HasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    CrLf = text.Contains("\r\n"),
                    Axes = root.Element("axis")
                };
                AddList(device.DxLists, root.Element("dx"));
                AddList(device.PovLists, root.Element("pov"));
                XElement f16 = root.Element("profileDefaultF16");
                if (f16 != null)
                {
                    AddList(device.DxLists, f16.Element("dx"));
                    AddList(device.PovLists, f16.Element("pov"));
                }
                devices.Add(device);
            }

            if (devices.Count == 0 && errors.Count == 0 && requireAny)
                errors.Add("No BMS device files (Setup.v100.*.xml) in " + configPath + ". Assign one axis or key of a connected device in the BMS launcher, or export a profile that binds a connected device.");
            return devices;
        }

        // The launcher writes a device file only once an axis or key of that device is assigned in it. For a connected
        // device of the profile without one, JoyPro starts from the launcher's stock file of that model (what the
        // launcher itself copies for known models) or from an empty file in the launcher's layout.
        private static void AddMissingDeviceFiles(string configPath, List<DeviceFile> files, GameExportRequest request, GameIoResult result)
        {
            if (request.Devices == null)
                return;
            foreach (string device in request.Bindings.Select(b => b.Device).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                string name;
                Guid guid;
                if (!GameDeviceDirectory.TryParseJoyProName(device, out name, out guid) || files.Any(f => f.Guid == guid))
                    continue;
                GameDevice connected = request.Devices.FindByJoyProName(device);
                if (connected == null)
                    continue;
                string product = string.IsNullOrEmpty(connected.InstanceName) ? name : connected.InstanceName;
                if (product.Length == 0 || product.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    continue;

                string root = Directory.GetParent(System.IO.Path.GetFullPath(configPath).TrimEnd('\\'))?.Parent?.FullName ?? string.Empty;
                string stock = System.IO.Path.Combine(root, "Launcher", "Stock", "Setup.v100." + product + " {Stock}.xml");
                string text = File.Exists(stock) ? File.ReadAllText(stock, Encoding.UTF8).TrimStart('\uFEFF') : BlankDeviceXml();
                XDocument document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                XElement element = document.Root;
                DeviceFile file = new DeviceFile
                {
                    Path = System.IO.Path.Combine(configPath, "Setup.v100." + product + " {" + guid.ToString().ToUpperInvariant() + "}.xml"),
                    Name = product,
                    Guid = guid,
                    Document = document,
                    HasBom = false,
                    CrLf = true,
                    Axes = element.Element("axis"),
                    Changed = true
                };
                AddList(file.DxLists, element.Element("dx"));
                AddList(file.PovLists, element.Element("pov"));
                files.Add(file);
                result.Steps.Add("Created the BMS device file for " + device + (File.Exists(stock) ? " from the launcher's stock file." : " (empty, in the launcher's layout)."));
            }
        }

        // What the launcher writes for a device with nothing assigned: 8 axes, 4 hats with 8 directions (press and
        // release callbacks), 128 buttons with 4 slots.
        private static string BlankDeviceXml()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<JoyAssgn xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n");
            sb.Append("  <axis>\r\n");
            for (int i = 0; i < 8; i++)
                sb.Append("    <AxAssgn>\r\n      <AxisName />\r\n      <AssgnDate>" + UnsetDate + "</AssgnDate>\r\n      <Invert>false</Invert>\r\n      <Saturation>None</Saturation>\r\n      <Deadzone>None</Deadzone>\r\n    </AxAssgn>\r\n");
            sb.Append("  </axis>\r\n  <pov>\r\n");
            for (int i = 0; i < 4; i++)
            {
                sb.Append("    <PovAssgn>\r\n      <direction>\r\n");
                for (int d = 0; d < 8; d++)
                    sb.Append("        <DirAssgn>\r\n          <Callback>\r\n            <string>" + Nothing + "</string>\r\n            <string>" + Nothing + "</string>\r\n          </Callback>\r\n          <SoundID>\r\n            <int>0</int>\r\n            <int>0</int>\r\n          </SoundID>\r\n        </DirAssgn>\r\n");
                sb.Append("      </direction>\r\n    </PovAssgn>\r\n");
            }
            sb.Append("  </pov>\r\n  <dx>\r\n");
            for (int i = 0; i < 128; i++)
            {
                sb.Append("    <DxAssgn>\r\n      <assign>\r\n");
                for (int a = 0; a < 4; a++)
                    sb.Append("        <Assgn>\r\n          <Callback>" + Nothing + "</Callback>\r\n          <Invoke>Default</Invoke>\r\n          <SoundID>0</SoundID>\r\n        </Assgn>\r\n");
                sb.Append("      </assign>\r\n    </DxAssgn>\r\n");
            }
            sb.Append("  </dx>\r\n</JoyAssgn>");
            return sb.ToString();
        }

        private static void AddList(List<XElement> target, XElement list)
        {
            if (list != null)
                target.Add(list);
        }

        private static XElement ActiveDx(DeviceFile device)
        {
            return device.DxLists.LastOrDefault(l => l.Elements("DxAssgn").Any());
        }

        private static XElement ActivePov(DeviceFile device)
        {
            return device.PovLists.LastOrDefault(l => l.Elements("PovAssgn").Any());
        }

        // <DxAssgn><assign><Assgn> x4: press, press+shift, release, release+shift.
        private static XElement DxAssgn(XElement dxList, int button, int slot)
        {
            XElement assign = dxList == null ? null : dxList.Elements("DxAssgn").ElementAtOrDefault(button);
            XElement slots = assign == null ? null : assign.Element("assign");
            return slots == null ? null : slots.Elements("Assgn").ElementAtOrDefault(slot);
        }

        private static XElement DxCallback(XElement dxList, int button, int slot)
        {
            XElement assgn = DxAssgn(dxList, button, slot);
            return assgn == null ? null : assgn.Element("Callback");
        }

        // <PovAssgn><direction><DirAssgn> x8 (U, UR, R, DR, D, DL, L, UL) <Callback><string> x2: normal, shifted.
        private static XElement PovCallback(XElement povList, int hat, int direction, int slot)
        {
            XElement pov = povList == null ? null : povList.Elements("PovAssgn").ElementAtOrDefault(hat);
            XElement directions = pov == null ? null : pov.Element("direction");
            XElement dir = directions == null ? null : directions.Elements("DirAssgn").ElementAtOrDefault(direction);
            XElement callback = dir == null ? null : dir.Element("Callback");
            return callback == null ? null : callback.Elements("string").ElementAtOrDefault(slot);
        }

        private static XElement AxisElement(DeviceFile file, int index)
        {
            return file.Axes == null ? null : file.Axes.Elements("AxAssgn").ElementAtOrDefault(index);
        }

        private static string Text(XElement element)
        {
            return element == null ? string.Empty : (element.Value ?? string.Empty).Trim();
        }

        private static string DeviceName(DeviceFile file, GameDeviceDirectory devices)
        {
            GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(file.Guid);
            return connected != null ? connected.JoyProName : GameDeviceDirectory.BuildJoyProName(file.Name, file.Guid);
        }

        private static ShiftButton FindShiftButton(List<DeviceFile> files)
        {
            foreach (DeviceFile file in files)
            {
                XElement dx = ActiveDx(file);
                int count = dx == null ? 0 : dx.Elements("DxAssgn").Count();
                for (int b = 0; b < count; b++)
                {
                    if (Text(DxCallback(dx, b, 0)) == ShiftCallback)
                        return new ShiftButton { File = file, Button = b };
                }
            }
            return null;
        }

        private static string ShiftReformer(string device, int button)
        {
            return "BMS Shift§" + device + "§" + GameInputTokens.ButtonToken(button) + "§False";
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            List<DeviceFile> files = LoadDeviceFiles(configPath, result.Errors);
            ShiftButton shift = FindShiftButton(files);
            string shiftReformer = shift == null ? null : ShiftReformer(DeviceName(shift.File, devices), shift.Button);

            string userCfg = ReadUserCfg(configPath);
            foreach (DeviceFile file in files)
            {
                string device = DeviceName(file, devices);
                for (int i = 0; i < 8; i++)
                {
                    XElement axis = AxisElement(file, i);
                    GameAction action = catalog.Find(AxisPrefix + Text(axis == null ? null : axis.Element("AxisName")));
                    if (action == null || !action.IsAxis)
                        continue;
                    AxisTuning tuning = ReadAxisTuning(axis, action.Id.Substring(AxisPrefix.Length), userCfg);
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = action.Id,
                        Device = device,
                        Input = GameInputTokens.AxisToken(i),
                        IsAxis = true,
                        Inverted = tuning.Inverted,
                        Deadzone = tuning.Deadzone,
                        SaturationX = tuning.SaturationX,
                        SaturationY = 1.0,
                        Tuning = tuning
                    });
                }

                XElement dx = ActiveDx(file);
                int buttons = dx == null ? 0 : dx.Elements("DxAssgn").Count();
                for (int b = 0; b < buttons; b++)
                {
                    for (int slot = 0; slot < PressSlots; slot++)
                        AddButton(result, catalog, Text(DxCallback(dx, b, slot)), device, GameInputTokens.ButtonToken(b), slot == 1 ? shiftReformer : string.Empty);
                }

                XElement pov = ActivePov(file);
                int hats = pov == null ? 0 : pov.Elements("PovAssgn").Count();
                for (int h = 0; h < hats; h++)
                {
                    for (int d = 0; d < 8; d++)
                    {
                        for (int slot = 0; slot < PressSlots; slot++)
                            AddButton(result, catalog, Text(PovCallback(pov, h, d, slot)), device, GameInputTokens.PovToken(h, d), slot == 1 ? shiftReformer : string.Empty);
                    }
                }
            }
            return result;
        }

        // A shifted slot needs the shift button (null reformer = no shift button, so the slot is not reachable).
        private static void AddButton(GameScanResult result, GameCatalog catalog, string callback, string device, string input, string reformer)
        {
            GameAction action = catalog.Find(callback);
            if (action == null || action.IsAxis || reformer == null)
                return;
            result.Bindings.Add(new GameInputBinding
            {
                ActionId = action.Id,
                Device = device,
                Input = input,
                Reformers = reformer.Length > 0 ? new List<string> { reformer } : new List<string>()
            });
        }

        private static int StepIndex(string value)
        {
            int index = Array.FindIndex(Steps, s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index;
        }

        // An axis entry as the bms model sees it: invert, the launcher steps (also as the base dead zone / saturation),
        // one-sided functions as Slider without a dead zone (the launcher hides it), and for pitch, roll, yaw and the
        // cursor the exponential curve and saturation of Falcon BMS User.cfg.
        private static AxisTuning ReadAxisTuning(XElement axis, string function, string userCfg)
        {
            AxisTuning tuning = new AxisTuning();
            tuning.Inverted = string.Equals(Text(axis.Element("Invert")), "true", StringComparison.OrdinalIgnoreCase);
            tuning.Slider = BmsSettings.OneSided.Contains(function);
            int dz = tuning.Slider ? 0 : StepIndex(Text(axis.Element("Deadzone")));
            int sat = StepIndex(Text(axis.Element("Saturation")));
            tuning.Deadzone = BmsAxisModel.DeadzoneFraction(dz);
            tuning.SaturationX = BmsAxisModel.SaturationFraction(sat);
            if (dz != 0)
                tuning.Native[BmsAxisModel.DeadzoneKey] = dz;
            if (sat != 0)
                tuning.Native[BmsAxisModel.SaturationKey] = sat;
            string suffix;
            if (userCfg != null && BmsSettings.ExpAxes.TryGetValue(function, out suffix))
            {
                int exp = CfgInt(BmsSettings.ReadUserCfg(userCfg, "g_nAxisExp_AXIS_" + suffix));
                int expSat = CfgInt(BmsSettings.ReadUserCfg(userCfg, "g_nAxisSat_AXIS_" + suffix));
                if (exp != 0)
                    tuning.Native[BmsAxisModel.ExpKey] = Math.Max(0, Math.Min(100, exp));
                if (expSat != 0)
                    tuning.Native[BmsAxisModel.ExpSatKey] = Math.Max(0, Math.Min(99, expSat));
            }
            return tuning;
        }

        private static int CfgInt(string value)
        {
            double number;
            return value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? (int)Math.Round(number) : 0;
        }

        private static string ReadUserCfg(string configPath)
        {
            string path = System.IO.Path.Combine(configPath, BmsSettings.UserCfg);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        // The shift button of an export: SimHotasPinkyShift in the exported binds wins over the one in the files.
        private static ShiftButton ResolveShift(List<DeviceFile> files, IEnumerable<GameInputBinding> bindings)
        {
            foreach (GameInputBinding binding in bindings)
            {
                string name;
                Guid guid;
                JoyInput input;
                if (!string.Equals(binding.ActionId, ShiftCallback, StringComparison.OrdinalIgnoreCase) || (binding.Reformers != null && binding.Reformers.Count > 0) ||
                    !GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid) ||
                    !GameInputTokens.TryParse(binding.Input, out input) || input.Kind != JoyInputKind.Button)
                    continue;
                DeviceFile file = files.FirstOrDefault(f => f.Guid == guid);
                if (file != null)
                    return new ShiftButton { File = file, Button = input.Index };
            }
            return FindShiftButton(files);
        }

        private static string Encode(List<DeviceFile> files, ShiftButton shift, GameInputBinding binding, GameAction action, out Target target)
        {
            target = null;
            if (binding.IsKeyboard)
                return "keyboard keys belong to the BMS key file; JoyPro writes only joystick inputs.";
            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid))
                return "device '" + binding.Device + "' has no DirectInput GUID.";
            DeviceFile file = files.FirstOrDefault(f => f.Guid == guid);
            if (file == null)
                return "BMS has no device file for '" + binding.Device + "' yet and it is not connected; connect it and export again.";

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";

            if (action.IsAxis)
            {
                if (input.Kind != JoyInputKind.Axis)
                    return "the action needs an axis.";
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                    return "BMS axes cannot have modifiers.";
                if (AxisElement(file, input.Index) == null)
                    return "the BMS device file has no axis " + binding.Input + ".";
                target = new Target { File = file, Kind = "AX", Index = input.Index, ActionId = action.Id, Value = action.Id.Substring(AxisPrefix.Length), Binding = binding };
                return string.Empty;
            }

            int slot = 0;
            if (binding.Reformers != null && binding.Reformers.Count > 0)
            {
                if (string.Equals(action.Id, ShiftCallback, StringComparison.OrdinalIgnoreCase))
                    return "the shift button itself cannot have a modifier.";
                if (shift == null)
                    return "BMS has one shift layer; bind 'Pinky Switch (DX Shift)' to a button and use that button as the modifier.";
                string[] parts = binding.Reformers[0].Split('§');
                string modName;
                Guid modGuid;
                JoyInput modInput;
                bool isShift = binding.Reformers.Count == 1 && parts.Length >= 3 &&
                    GameDeviceDirectory.TryParseJoyProName(parts[1], out modName, out modGuid) && modGuid == shift.File.Guid &&
                    GameInputTokens.TryParse(parts[2], out modInput) && modInput.Kind == JoyInputKind.Button && modInput.Index == shift.Button;
                if (!isShift)
                    return "BMS has one shift layer; the only modifier can be its shift button (" + shift.File.Name + " " + GameInputTokens.ButtonToken(shift.Button) + ").";
                slot = 1;
            }

            if (input.Kind == JoyInputKind.Button)
            {
                if (DxCallback(ActiveDx(file), input.Index, slot) == null)
                    return "the BMS device file has no " + binding.Input + ".";
                target = new Target { File = file, Kind = "DX", Index = input.Index, Slot = slot, ActionId = action.Id, Value = action.Id, Binding = binding };
                return string.Empty;
            }
            if (input.Kind == JoyInputKind.Pov)
            {
                if (PovCallback(ActivePov(file), input.Index, input.Direction, slot) == null)
                    return "the BMS device file has no hat " + (input.Index + 1).ToString(CultureInfo.InvariantCulture) + ".";
                target = new Target { File = file, Kind = "POV", Index = input.Index, Direction = input.Direction, Slot = slot, ActionId = action.Id, Value = action.Id, Binding = binding };
                return string.Empty;
            }
            return "'" + binding.Input + "' is an axis; bind it to an axis action.";
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            List<DeviceFile> files = LoadDeviceFiles(request.ConfigPath, result.Errors, false);
            if (result.Errors.Count > 0)
                return result;
            AddMissingDeviceFiles(request.ConfigPath, files, request, result);
            if (files.Count == 0)
            {
                result.Errors.Add("No BMS device files (Setup.v100.*.xml) in " + request.ConfigPath + " and no connected device in the profile to create one for.");
                return result;
            }
            ShiftButton shift = ResolveShift(files, request.Bindings);

            Dictionary<string, Target> wanted = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, AxisTuning>> axes = new List<KeyValuePair<string, AxisTuning>>();
            HashSet<string> wantedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                Target target = null;
                string reason = action == null ? "not in the BMS catalog." : Encode(files, shift, binding, action, out target);
                Target existing;
                if (reason.Length == 0 && wanted.TryGetValue(target.OwnerToken, out existing))
                    reason = "the input is already used by relation '" + existing.Binding.RelationName + "'.";
                if (reason.Length == 0 && target.Kind == "AX" && !wantedAxes.Add(target.Value))
                    reason = "BMS gives each axis function to one physical axis only.";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                wanted[target.OwnerToken] = target;
            }

            string ownershipBase = OwnershipBase(request.ConfigPath);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipBase, result.Warnings);
            if (request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite)
            {
                ReleaseOwned(files, owned, wanted);
                owned.Clear();
            }
            if (request.Mode == GameExportMode.Clean || request.NukeUnusedDevices)
            {
                // BMS is flight only: clean clears the devices JoyPro writes, nuke the ones it does not use.
                HashSet<Guid> managed = new HashSet<Guid>(wanted.Values.Select(t => t.File.Guid));
                bool keepShift = wanted.Values.Any(t => t.Slot == 1);
                foreach (DeviceFile file in files)
                {
                    bool used = managed.Contains(file.Guid);
                    if (used ? request.Mode != GameExportMode.Clean : !request.NukeUnusedDevices)
                        continue;
                    ReleaseDevice(file, wanted, keepShift && shift != null && shift.File == file ? shift.Button : -1);
                }
            }

            foreach (Target target in wanted.Values.OrderBy(t => t.File.Path, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Kind).ThenBy(t => t.Index).ThenBy(t => t.Direction).ThenBy(t => t.Slot))
            {
                string current = CurrentValue(target);
                bool free = current.Length == 0 || current == Nothing;
                if (current != target.Value && !free)
                {
                    if (request.Mode == GameExportMode.Merge)
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): the input already has '" + current + "' in BMS; merge keeps it.");
                        continue;
                    }
                    result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + ") replaced '" + current + "' on its input.");
                }
                if (target.Kind == "AX" && request.Mode == GameExportMode.Merge)
                {
                    string claimedBy = OtherAxisClaim(files, target);
                    if (claimedBy.Length > 0)
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): BMS already has this axis on " + claimedBy + "; merge keeps it.");
                        continue;
                    }
                }

                if (target.Kind == "AX" && BmsSettings.OneSided.Contains(target.Value) && current == target.Value)
                {
                    XElement axis = AxisElement(target.File, target.Index);
                    string legacy = Text(axis == null ? null : axis.Element("Deadzone"));
                    if (legacy.Length > 0 && legacy != "None")
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): BMS has no dead zone on one-sided axes (the launcher hides it; a stored one acts in the middle of the travel); '" + legacy + "' was reset to None.");
                }
                Assign(target, files);
                GameOwnershipStore.Add(owned, target.ActionId, target.OwnerToken);
                result.BindingsWritten++;
                if (target.Kind == "AX")
                    axes.Add(new KeyValuePair<string, AxisTuning>(target.Value, AxisTuningOf(target.Binding, target.Value)));
            }

            ApplyDetents(files, result);
            SaveDeviceFiles(files, request.DryRun, result);
            if (!request.DryRun)
                GameOwnershipStore.Save(ownershipBase, GameId, owned);
            WriteGameSettings(request.ConfigPath, request.DryRun, request.Mode == GameExportMode.Merge, axes, result);
            return result;
        }

        private static void SaveDeviceFiles(List<DeviceFile> files, bool dryRun, GameIoResult result)
        {
            int changed = files.Count(f => f.Changed);
            if (dryRun)
            {
                result.Steps.Add("Dry run: " + changed.ToString(CultureInfo.InvariantCulture) + " BMS device file(s) would change.");
                return;
            }
            foreach (DeviceFile file in files.Where(f => f.Changed))
            {
                GameExportSafety.BackupBeforeOverwrite(file.Path, result.Warnings);
                try
                {
                    Save(file);
                    result.FilesWritten.Add(file.Path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add("Could not write '" + file.Path + "': " + ex.Message);
                }
            }
            result.Steps.Add(changed == 0
                ? "The BMS device files already match."
                : changed.ToString(CultureInfo.InvariantCulture) + " BMS device file(s) changed. Start BMS through its launcher so it rebuilds the key file and axis setup.");
        }

        // ---- game settings (engine ffb set --game FalconBMS) --------------------------------------------------------

        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && FfbSettings.DevicesFor(InternalDataManagement.FfbOverrides, GameId).Any();
        }

        // An export without BMS binds: only the game-wide settings and the throttle detents.
        public GameIoResult WriteSettings(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            List<DeviceFile> files = LoadDeviceFiles(request.ConfigPath, result.Errors, false);
            if (result.Errors.Count > 0)
                return result;
            ApplyDetents(files, result);
            if (files.Any(f => f.Changed))
                SaveDeviceFiles(files, request.DryRun, result);
            WriteGameSettings(request.ConfigPath, request.DryRun, false, new List<KeyValuePair<string, AxisTuning>>(), result);
            return result;
        }

        // The launcher's AB/IDLE detents of a throttle device: <detentPosition> (first child of <JoyAssgn>, 0..65536 =
        // share of the throttle travel from idle; AB 65536 / IDLE 0 = no detent), from the device's settings in %.
        private static void ApplyDetents(List<DeviceFile> files, GameIoResult result)
        {
            Dictionary<string, string> store = InternalDataManagement.FfbOverrides;
            if (store == null)
                return;
            foreach (string device in FfbSettings.DevicesFor(store, BmsSettings.Game))
            {
                if (device == FfbSettings.AnyDevice)
                    continue;
                Dictionary<string, double> values = FfbSettings.Resolve(store, device, BmsSettings.Game, null);
                double ab;
                double idle;
                bool hasAb = values.TryGetValue(BmsSettings.AbDetent, out ab);
                bool hasIdle = values.TryGetValue(BmsSettings.IdleDetent, out idle);
                if (!hasAb && !hasIdle)
                    continue;
                string name;
                Guid guid;
                DeviceFile file = GameDeviceDirectory.TryParseJoyProName(device, out name, out guid) ? files.FirstOrDefault(f => f.Guid == guid) : null;
                if (file == null)
                {
                    result.Warnings.Add("Throttle detents of '" + device + "' not written: BMS has no device file for it.");
                    continue;
                }
                XElement root = file.Document.Root;
                XElement detents = root.Element("detentPosition");
                int currentAb = DetentValue(detents, "AB", 65536);
                int currentIdle = DetentValue(detents, "IDLE", 0);
                int newAb = hasAb ? (int)Math.Round(Math.Max(0, Math.Min(100, ab)) * 65536 / 100.0) : currentAb;
                int newIdle = hasIdle ? (int)Math.Round(Math.Max(0, Math.Min(100, idle)) * 65536 / 100.0) : currentIdle;
                if (newIdle >= newAb)
                {
                    result.Warnings.Add("Throttle detents of '" + device + "' not written: the IDLE detent must be below the AB detent.");
                    continue;
                }
                if (detents == null)
                {
                    // XmlSerializer order: detentPosition comes before axis.
                    detents = new XElement("detentPosition", new XText("\n    "), new XElement("AB", newAb.ToString(CultureInfo.InvariantCulture)), new XText("\n    "),
                        new XElement("IDLE", newIdle.ToString(CultureInfo.InvariantCulture)), new XText("\n  "));
                    XNode first = root.FirstNode;
                    if (first is XText)
                        first.AddAfterSelf(detents, new XText("\n  "));
                    else
                        root.AddFirst(new XText("\n  "), detents);
                    file.Changed = true;
                }
                else if (newAb != currentAb || newIdle != currentIdle || detents.Element("AB") == null || detents.Element("IDLE") == null)
                {
                    SetDetent(detents, "AB", newAb);
                    SetDetent(detents, "IDLE", newIdle);
                    file.Changed = true;
                }
                else
                    continue;
                result.Steps.Add("Throttle detents of " + device + ": AB " + newAb.ToString(CultureInfo.InvariantCulture) + ", IDLE " + newIdle.ToString(CultureInfo.InvariantCulture) + " (of 65536).");
            }
        }

        private static int DetentValue(XElement detents, string name, int fallback)
        {
            int value;
            return detents != null && int.TryParse(Text(detents.Element(name)), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static void SetDetent(XElement detents, string name, int value)
        {
            XElement element = detents.Element(name);
            if (element == null)
            {
                detents.Add(new XText("  "), new XElement(name, value.ToString(CultureInfo.InvariantCulture)), new XText("\n  "));
                return;
            }
            element.Value = value.ToString(CultureInfo.InvariantCulture);
        }

        // Falcon BMS User.cfg (exponential curve/saturation of the exported pitch, roll, yaw and cursor axes; game-wide
        // cfg settings), Feedback.ini (effect gains) and Enable Force Feedback in the pilot file. Files the launcher
        // rebuilds on Launch (axismapping.dat, joystick.cal, the override block of User.cfg) are never written.
        private static void WriteGameSettings(string configPath, bool dryRun, bool merge, List<KeyValuePair<string, AxisTuning>> axes, GameIoResult result)
        {
            Dictionary<string, double> settings = InternalDataManagement.FfbOverrides == null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, BmsSettings.Game, null);

            string cfgPath = System.IO.Path.Combine(configPath, BmsSettings.UserCfg);
            string cfgText = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : null;
            List<KeyValuePair<string, string>> gameValues = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> key in BmsSettings.CfgKeys)
            {
                double value;
                if (settings.TryGetValue(key.Key, out value))
                    gameValues.Add(new KeyValuePair<string, string>(key.Value, BmsSettings.CfgValue(key.Key, value)));
            }
            List<KeyValuePair<string, string>> axisValues = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, AxisTuning> axis in axes)
            {
                string suffix;
                int exp = BmsAxisModel.Exp(axis.Value);
                int sat = BmsAxisModel.ExpSat(axis.Value);
                if (!BmsSettings.ExpAxes.TryGetValue(axis.Key, out suffix))
                {
                    if (exp != 0 || sat != 0)
                        result.Warnings.Add(axis.Key + ": BMS has an exponential curve and saturation only for pitch, roll, yaw and cursor X/Y; not written.");
                    continue;
                }
                foreach (KeyValuePair<string, int> v in new[] { new KeyValuePair<string, int>("g_nAxisExp_AXIS_" + suffix, exp), new KeyValuePair<string, int>("g_nAxisSat_AXIS_" + suffix, sat) })
                {
                    // Only values the tuning sets (a tuning inherited from the base sets none); 0 is the game's
                    // default, so no line is added for it.
                    string key = v.Key.StartsWith("g_nAxisExp", StringComparison.Ordinal) ? BmsAxisModel.ExpKey : BmsAxisModel.ExpSatKey;
                    if (axis.Value.Native == null || !axis.Value.Native.ContainsKey(key))
                        continue;
                    if (v.Value == 0 && BmsSettings.ReadUserCfg(cfgText, v.Key) == null)
                        continue;
                    axisValues.Add(new KeyValuePair<string, string>(v.Key, v.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }
            if (gameValues.Count > 0 || axisValues.Count > 0)
            {
                string text = cfgText ?? string.Empty;
                List<string> changes = new List<string>();
                bool changed = BmsSettings.ApplyUserCfg(ref text, gameValues, false, changes, result.Warnings);
                changed |= BmsSettings.ApplyUserCfg(ref text, axisValues, merge, changes, result.Warnings);
                if (changed)
                    WriteText(cfgPath, text, dryRun, changes, result, null);
            }

            Dictionary<string, double> feedback = settings.Where(s => BmsSettings.GainKeys.ContainsKey(s.Key) || string.Equals(s.Key, BmsSettings.AutoCenterStrength, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
            if (feedback.Count > 0)
            {
                string iniPath = System.IO.Path.Combine(configPath, BmsSettings.FeedbackIni);
                if (!File.Exists(iniPath))
                    result.Warnings.Add("Force feedback effects not written: " + iniPath + " does not exist (BMS ships it in User\\Config).");
                else
                {
                    Encoding latin1 = Encoding.GetEncoding(28591);
                    string text = File.ReadAllText(iniPath, latin1);
                    List<string> changes = new List<string>();
                    if (BmsSettings.ApplyFeedback(ref text, feedback, changes, result.Warnings))
                        WriteText(iniPath, text, dryRun, changes, result, latin1);
                }
            }

            double enabled;
            if (settings.TryGetValue(BmsSettings.FfbEnabled, out enabled))
                WriteEnableFfb(configPath, enabled != 0, dryRun, result);
        }

        // The game-wide settings the files hold, for an import (GameCommands stores them as the game's settings):
        // User.cfg variables JoyPro manages, the Feedback.ini gains and Enable Force Feedback of the pilot file.
        public Dictionary<string, double> ReadSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(configPath) || !Directory.Exists(configPath))
                return values;
            string cfgPath = System.IO.Path.Combine(configPath, BmsSettings.UserCfg);
            string cfgText = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : null;
            foreach (KeyValuePair<string, string> key in BmsSettings.CfgKeys)
            {
                string raw = BmsSettings.ReadUserCfg(cfgText, key.Value);
                double number;
                if (raw != null && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    values[key.Key] = number;
            }
            string iniPath = System.IO.Path.Combine(configPath, BmsSettings.FeedbackIni);
            if (File.Exists(iniPath))
            {
                foreach (KeyValuePair<string, double> gain in BmsSettings.ReadFeedback(File.ReadAllText(iniPath, Encoding.GetEncoding(28591))))
                    values[gain.Key] = gain.Value;
            }
            string pop = BmsSettings.FindPilotFile(configPath, warnings);
            if (pop != null)
            {
                byte[] bytes = File.ReadAllBytes(pop);
                if (bytes.Length == BmsSettings.PopSize)
                    values[BmsSettings.FfbEnabled] = bytes[BmsSettings.PopEnableFfb] != 0 ? 1 : 0;
            }
            return values;
        }

        // UTF-8 (BOM kept as the file had it; the launcher writes none) unless an encoding is given.
        private static void WriteText(string path, string text, bool dryRun, List<string> changes, GameIoResult result, Encoding encoding)
        {
            if (dryRun)
            {
                result.Steps.AddRange(changes.Select(c => "Dry run: would " + c));
                return;
            }
            bool bom = false;
            if (File.Exists(path) && encoding == null)
            {
                byte[] head = File.ReadAllBytes(path);
                bom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }
            string content = text.TrimStart('﻿');
            WriteKeepingAttributes(path, result, () => File.WriteAllText(path, content, encoding ?? new UTF8Encoding(bom)));
            result.Steps.AddRange(changes);
        }

        // Enable Force Feedback: PlayerOptionsClass.enableFFB, byte 388 of the 468-byte pilot file (PDB member offset; the
        // launcher's SavePop rewrites bytes 0, 12, 336-351, 384, 389, 390 and 410 on Launch and keeps 388).
        private static void WriteEnableFfb(string configPath, bool enabled, bool dryRun, GameIoResult result)
        {
            string pop = BmsSettings.FindPilotFile(configPath, result.Warnings);
            if (pop == null)
            {
                result.Warnings.Add("Enable Force Feedback not written: no pilot file (<callsign>.pop) in " + configPath + " yet; start BMS once with the pilot.");
                return;
            }
            byte[] bytes = File.ReadAllBytes(pop);
            if (bytes.Length != BmsSettings.PopSize)
            {
                result.Warnings.Add("Enable Force Feedback not written: " + pop + " has " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes; JoyPro knows the 468-byte pilot file of BMS 4.36+.");
                return;
            }
            byte wanted = (byte)(enabled ? 1 : 0);
            if (bytes[BmsSettings.PopEnableFfb] == wanted)
                return;
            string change = System.IO.Path.GetFileName(pop) + ": Enable Force Feedback " + (enabled ? "on" : "off");
            if (dryRun)
            {
                result.Steps.Add("Dry run: would set " + change);
                return;
            }
            bytes[BmsSettings.PopEnableFfb] = wanted;
            WriteKeepingAttributes(pop, result, () => File.WriteAllBytes(pop, bytes));
            result.Steps.Add(change);
        }

        private static void WriteKeepingAttributes(string path, GameIoResult result, Action write)
        {
            GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
            FileAttributes attributes = File.Exists(path) ? File.GetAttributes(path) : FileAttributes.Normal;
            bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
            try
            {
                if (readOnly)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                write();
                result.FilesWritten.Add(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Errors.Add("Could not write '" + path + "': " + ex.Message);
            }
            finally
            {
                if (readOnly && File.Exists(path))
                    File.SetAttributes(path, attributes);
            }
        }

        private static string CurrentValue(Target target)
        {
            if (target.Kind == "AX")
            {
                XElement axis = AxisElement(target.File, target.Index);
                return Text(axis == null ? null : axis.Element("AxisName"));
            }
            if (target.Kind == "DX")
                return Text(DxCallback(ActiveDx(target.File), target.Index, target.Slot));
            return Text(PovCallback(ActivePov(target.File), target.Index, target.Direction, target.Slot));
        }

        private static string OtherAxisClaim(List<DeviceFile> files, Target target)
        {
            foreach (DeviceFile file in files)
            {
                for (int i = 0; i < 8; i++)
                {
                    XElement axis = AxisElement(file, i);
                    if ((file != target.File || i != target.Index) && axis != null && Text(axis.Element("AxisName")) == target.Value)
                        return file.Name + " " + GameInputTokens.AxisToken(i);
                }
            }
            return string.Empty;
        }

        private static void Assign(Target target, List<DeviceFile> files)
        {
            if (target.Kind == "AX")
            {
                // One physical axis per function, like the launcher: older claims of the function are released.
                foreach (DeviceFile file in files)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        XElement other = AxisElement(file, i);
                        if ((file != target.File || i != target.Index) && other != null && Text(other.Element("AxisName")) == target.Value && SetAxis(other, null, false, 0, 0))
                            file.Changed = true;
                    }
                }
                GameInputBinding binding = target.Binding;
                AxisTuning tuning = AxisTuningOf(binding, target.Value);
                if (SetAxis(AxisElement(target.File, target.Index), target.Value, tuning.Inverted, BmsAxisModel.DeadzoneStep(tuning), BmsAxisModel.SaturationStep(tuning)))
                    target.File.Changed = true;
                return;
            }

            // Every copy of the list (top level and profileDefaultF16) gets the callback, so all launcher versions agree.
            foreach (XElement list in target.Kind == "DX" ? target.File.DxLists : target.File.PovLists)
            {
                if (target.Kind == "DX")
                    SetDxSlot(target.File, DxAssgn(list, target.Index, target.Slot), target.Value);
                else
                    SetText(target.File, PovCallback(list, target.Index, target.Direction, target.Slot), target.Value);
            }
        }

        // A new callback starts with the default invoke (press = on, release = off); the sound stays.
        private static void SetDxSlot(DeviceFile file, XElement assgn, string callback)
        {
            if (assgn == null)
                return;
            XElement element = assgn.Element("Callback");
            if (element == null || Text(element) == callback)
                return;
            element.Value = callback;
            SetText(file, assgn.Element("Invoke"), "Default");
            file.Changed = true;
        }

        private static void SetText(DeviceFile file, XElement element, string value)
        {
            if (element != null && element.Value != value)
            {
                element.Value = value;
                file.Changed = true;
            }
        }

        // Returns true when the axis entry changed. A new function gets the current time as AssgnDate (the launcher
        // gives a function to the newest claim); a released axis gets the launcher's "unset" date and default tuning.
        private static bool SetAxis(XElement axis, string name, bool invert, int deadzoneStep, int saturationStep)
        {
            if (axis == null)
                return false;
            string before = axis.ToString(SaveOptions.DisableFormatting);
            XElement nameElement = axis.Element("AxisName");
            XElement date = axis.Element("AssgnDate");
            string newName = name ?? string.Empty;
            if (nameElement != null && Text(nameElement) != newName)
            {
                if (newName.Length == 0)
                    nameElement.RemoveNodes();
                else
                    nameElement.Value = newName;
                if (date != null)
                    date.Value = newName.Length == 0 ? UnsetDate : DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            }
            SetValue(axis.Element("Invert"), newName.Length > 0 && invert ? "true" : "false");
            SetValue(axis.Element("Saturation"), newName.Length > 0 ? Steps[saturationStep] : "None");
            SetValue(axis.Element("Deadzone"), newName.Length > 0 && !BmsSettings.OneSided.Contains(newName) ? Steps[deadzoneStep] : "None");
            return before != axis.ToString(SaveOptions.DisableFormatting);
        }

        // The axis values the export writes: the resolved tuning (bms model), else the bind's plain values; one-sided
        // functions are sliders (no dead zone, as the launcher shows them).
        private static AxisTuning AxisTuningOf(GameInputBinding binding, string function)
        {
            AxisTuning tuning = binding.Tuning != null ? binding.Tuning.Clone() : new AxisTuning
            {
                Inverted = binding.Inverted,
                Deadzone = binding.Deadzone,
                SaturationX = binding.SaturationX,
                SaturationY = binding.SaturationY
            };
            tuning.Inverted = binding.Inverted;
            if (BmsSettings.OneSided.Contains(function))
                tuning.Slider = true;
            return tuning;
        }

        private static void SetValue(XElement element, string value)
        {
            if (element != null && element.Value != value)
                element.Value = value;
        }

        // Inputs JoyPro wrote before and no longer wants are released, unless the user changed them in the launcher.
        private static void ReleaseOwned(List<DeviceFile> files, Dictionary<string, HashSet<string>> owned, Dictionary<string, Target> wanted)
        {
            foreach (KeyValuePair<string, HashSet<string>> entry in owned)
            {
                foreach (string token in entry.Value)
                {
                    if (wanted.ContainsKey(token))
                        continue;
                    Target target = ParseOwner(files, token, entry.Key);
                    if (target != null && CurrentValue(target) == target.Value)
                        Clear(target);
                }
            }
        }

        private static Target ParseOwner(List<DeviceFile> files, string token, string actionId)
        {
            string[] parts = token.Split('|');
            Guid guid;
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out guid))
                return null;
            DeviceFile file = files.FirstOrDefault(f => f.Guid == guid);
            string[] key = parts[1].Split(':');
            if (file == null || key.Length < 2)
                return null;
            int[] numbers = new int[key.Length - 1];
            for (int i = 1; i < key.Length; i++)
            {
                if (!int.TryParse(key[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i - 1]) || numbers[i - 1] < 0)
                    return null;
            }
            if (key[0] == "AX" && numbers.Length == 1 && actionId.StartsWith(AxisPrefix, StringComparison.OrdinalIgnoreCase))
                return new Target { File = file, Kind = "AX", Index = numbers[0], ActionId = actionId, Value = actionId.Substring(AxisPrefix.Length) };
            if (key[0] == "DX" && numbers.Length == 2)
                return new Target { File = file, Kind = "DX", Index = numbers[0], Slot = numbers[1], ActionId = actionId, Value = actionId };
            if (key[0] == "POV" && numbers.Length == 3)
                return new Target { File = file, Kind = "POV", Index = numbers[0], Direction = numbers[1], Slot = numbers[2], ActionId = actionId, Value = actionId };
            return null;
        }

        private static void Clear(Target target)
        {
            if (target.Kind == "AX")
            {
                if (SetAxis(AxisElement(target.File, target.Index), null, false, 0, 0))
                    target.File.Changed = true;
                return;
            }
            foreach (XElement list in target.Kind == "DX" ? target.File.DxLists : target.File.PovLists)
            {
                if (target.Kind == "DX")
                    SetDxSlot(target.File, DxAssgn(list, target.Index, target.Slot), Nothing);
                else
                    SetText(target.File, PovCallback(list, target.Index, target.Direction, target.Slot), Nothing);
            }
        }

        // Every callback (all four button slots, both hat layers) and axis function of the device that JoyPro does not
        // set is released. The shift button stays while shifted binds need it.
        private static void ReleaseDevice(DeviceFile file, Dictionary<string, Target> wanted, int keepShiftButton)
        {
            HashSet<string> keep = new HashSet<string>(wanted.Values.Where(t => t.File == file).Select(t => t.Key), StringComparer.Ordinal);
            for (int i = 0; i < 8; i++)
            {
                XElement axis = AxisElement(file, i);
                string name = Text(axis == null ? null : axis.Element("AxisName"));
                if (name.Length > 0 && !keep.Contains("AX:" + i.ToString(CultureInfo.InvariantCulture)) && SetAxis(axis, null, false, 0, 0))
                    file.Changed = true;
            }
            foreach (XElement dx in file.DxLists)
            {
                int buttons = dx.Elements("DxAssgn").Count();
                for (int b = 0; b < buttons; b++)
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        XElement assgn = DxAssgn(dx, b, slot);
                        string callback = Text(assgn == null ? null : assgn.Element("Callback"));
                        bool isKeptShift = b == keepShiftButton && slot == 0 && callback == ShiftCallback;
                        if (callback.Length > 0 && callback != Nothing && !isKeptShift && !keep.Contains("DX:" + b.ToString(CultureInfo.InvariantCulture) + ":" + slot.ToString(CultureInfo.InvariantCulture)))
                            SetDxSlot(file, assgn, Nothing);
                    }
                }
            }
            foreach (XElement pov in file.PovLists)
            {
                int hats = pov.Elements("PovAssgn").Count();
                for (int h = 0; h < hats; h++)
                {
                    for (int d = 0; d < 8; d++)
                    {
                        for (int slot = 0; slot < PressSlots; slot++)
                        {
                            XElement element = PovCallback(pov, h, d, slot);
                            string key = "POV:" + h.ToString(CultureInfo.InvariantCulture) + ":" + d.ToString(CultureInfo.InvariantCulture) + ":" + slot.ToString(CultureInfo.InvariantCulture);
                            string callback = Text(element);
                            if (callback.Length > 0 && callback != Nothing && !keep.Contains(key))
                                SetText(file, element, Nothing);
                        }
                    }
                }
            }
        }

        // Written like the launcher's XmlSerializer: same declaration, original whitespace and line ends, BOM only if
        // the file had one. A read-only file stays read-only.
        private static void Save(DeviceFile file)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(file.HasBom),
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = file.CrLf ? "\r\n" : "\n",
                OmitXmlDeclaration = file.Document.Declaration == null
            };
            FileAttributes attributes = File.Exists(file.Path) ? File.GetAttributes(file.Path) : FileAttributes.Normal;
            bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly)
                File.SetAttributes(file.Path, attributes & ~FileAttributes.ReadOnly);
            try
            {
                using (FileStream stream = new FileStream(file.Path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    file.Document.Save(writer);
            }
            finally
            {
                if (readOnly)
                    File.SetAttributes(file.Path, attributes);
            }
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            List<string> errors = new List<string>();
            List<DeviceFile> files = LoadDeviceFiles(configPath, errors);
            if (errors.Count > 0)
                return;
            ShiftButton shift = FindShiftButton(files);
            GameCatalog catalog = GameCatalogStore.Load(this, result.Warnings);
            string ownershipBase = OwnershipBase(configPath);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipBase, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                GameAction action = catalog.Find(binding.ActionId);
                Target target;
                if (action != null && Encode(files, shift, binding, action, out target).Length == 0)
                    GameOwnershipStore.Add(owned, action.Id, target.OwnerToken);
            }
            GameOwnershipStore.Save(ownershipBase, GameId, owned);
            result.FilesWritten.Add(ownershipBase + GameOwnershipStore.Suffix);
        }

        // Callbacks of the cockpit and flight sections of "BMS - Full.key" (visible entries only) plus the launcher's
        // axis functions. The source is the key file or the User\Config folder that holds it.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string keyFile = string.IsNullOrWhiteSpace(sourcePath) ? FindFullKeyFile() : sourcePath.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(keyFile) && Directory.Exists(keyFile))
                keyFile = System.IO.Path.Combine(keyFile, FullKeyFile);
            if (string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile))
            {
                result.Errors.Add("'" + FullKeyFile + "' was not found. Pass the BMS User\\Config folder or the key file.");
                return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = keyFile };
            foreach (string axis in AxisNames)
                catalog.Add(new GameAction { Id = AxisPrefix + axis, Title = axis.Replace('_', ' '), IsAxis = true, Category = "Axes" });

            string section = null;
            foreach (string line in File.ReadAllLines(keyFile, Encoding.GetEncoding(28591)))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;
                int quote = trimmed.IndexOf('"');
                string description = quote < 0 ? string.Empty : trimmed.Substring(quote + 1).TrimEnd().TrimEnd('"').Trim();
                string[] tokens = (quote < 0 ? trimmed : trimmed.Substring(0, quote)).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                // Callback SoundID 0 Key Modifiers ComboKey ComboModifiers Visibility "Description"
                if (tokens.Length < 8)
                    continue;
                if (tokens[7] == "-1")
                {
                    // Headers: "1. UI & 3RD PARTY SOFTWARE" (chapter) and "======== 2.04 MANUAL TRIM PANEL ========".
                    Match header = FlightSection.Match(description);
                    section = header.Success ? header.Groups["num"].Value + " " + Regex.Replace(header.Groups["title"].Value, @"\s+", " ").Trim() : null;
                    continue;
                }
                if (section == null || tokens[0] == Nothing || tokens[7] != "1")
                    continue;
                catalog.Add(new GameAction
                {
                    Id = tokens[0],
                    Title = description.Length > 0 ? description.Replace('\t', ' ') : tokens[0],
                    IsAxis = false,
                    Category = section
                });
            }
            result.Steps.Add("Callbacks read from " + keyFile);
            return catalog;
        }
    }
}
