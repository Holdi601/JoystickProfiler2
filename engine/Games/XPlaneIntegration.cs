using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // X-Plane 11 and X-Plane 12 joystick preferences, one game per version (each with its own installation,
    // Output\preferences folder and catalog): "X-Plane Joystick Settings.prf" or a control profile's
    // "<Name> - Joysticks.prf" (same format in both versions; X-Plane 12 adds axis use codes 67-80, the copilot
    // control response and _joy_ffb_axis lines, X-Plane 11 has _joy_damp). Each of the 20 device slots owns 25 axis
    // entries (_joy_AXIS_use/_joy_rev_axis) and 160 button entries (_joy_BUTN_use). JoyPro only changes entries that
    // hold or get a cataloged flight axis or command; calibration, other commands and all other lines stay as they
    // are. See docs/games/xplane.md.
    internal sealed class XPlaneIntegration : IGameIntegration, IGameOwnershipTracking, IGameSettingsReader
    {
        private readonly int version;

        // version 11 or 12.
        public XPlaneIntegration(int version)
        {
            this.version = version == 11 ? 11 : 12;
        }

        public int Version { get { return version; } }
        public string GameId { get { return "XPlane" + version.ToString(CultureInfo.InvariantCulture); } }
        public string DisplayName { get { return "X-Plane " + version.ToString(CultureInfo.InvariantCulture); } }
        // "xplane" / "xp" (the CLI area of the single X-Plane game before 11 and 12 were split) stay with X-Plane 12.
        public IReadOnlyList<string> Aliases { get { return version == 11 ? new[] { "xplane11", "xp11" } : new[] { "xplane12", "xp12", "xplane", "xp" }; } }
        public string DbFolder { get { return GameId; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Experimental. Close X-Plane before exporting. Start this X-Plane version once with your devices connected: it creates Output\\preferences\\X-Plane Joystick Settings.prf, the file JoyPro exports to. With control profiles, export into the profile's '<Name> - Joysticks.prf'. Keyboard shortcuts and modifiers are not written." +
                       (version == 11 ? " X-Plane 11 has no copilot axes, wheel brakes or throttle 9 (X-Plane 12 only)." : string.Empty);
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "X-Plane" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        // Highest axis use code of the version: X-Plane 11's use-code table ends with joy_use_thro_hrz (66), X-Plane 12
        // adds 67-80 (string tables of both X-Plane.exe).
        private int MaxAxisCode { get { return version == 11 ? 66 : 80; } }

        private const int SlotCount = 20;
        private const int AxesPerSlot = 25;
        private const int ButtonsPerSlot = 160;
        private const int HatButtons = 8;
        private const string NoCommand = "sim/none/none";
        private static readonly Regex UniqueId = new Regex(@"^VID:(?<vid>\d+)PID:(?<pid>\d+)$", RegexOptions.Compiled);

        private sealed class XpAxis
        {
            public int Code;
            public string Id;
            public string Title;
        }

        // sim/joystick/joystick_axis_assignments values that control the aircraft (view, camera, VR and custom-command
        // axes are left alone).
        private static readonly XpAxis[] Axes = BuildAxes();

        private static XpAxis[] BuildAxes()
        {
            List<XpAxis> axes = new List<XpAxis>();
            Action<int, string> add = (code, title) => axes.Add(new XpAxis
            {
                Code = code,
                Id = "axis/" + Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_'),
                Title = title
            });
            add(1, "Pitch");
            add(2, "Roll");
            add(3, "Yaw");
            add(4, "Throttle");
            add(5, "Collective");
            add(6, "Left toe brake");
            add(7, "Right toe brake");
            add(8, "Prop");
            add(9, "Mixture");
            add(10, "Carb heat");
            add(11, "Flaps");
            add(12, "Thrust vector");
            add(13, "Wing sweep");
            add(14, "Speedbrakes");
            add(15, "Displacement");
            add(16, "Reverse");
            add(17, "Elevator trim");
            add(18, "Aileron trim");
            add(19, "Rudder trim");
            for (int i = 1; i <= 4; i++)
            {
                add(19 + i, "Throttle " + i.ToString(CultureInfo.InvariantCulture));
                add(23 + i, "Prop " + i.ToString(CultureInfo.InvariantCulture));
                add(27 + i, "Mixture " + i.ToString(CultureInfo.InvariantCulture));
                add(31 + i, "Reverse " + i.ToString(CultureInfo.InvariantCulture));
            }
            add(36, "Landing gear");
            add(37, "Nosewheel tiller");
            add(38, "Backup throttle");
            add(39, "Cowl flaps");
            add(47, "Gun bomb left right");
            add(48, "Gun bomb up down");
            for (int i = 5; i <= 8; i++)
                add(48 + i, "Throttle " + i.ToString(CultureInfo.InvariantCulture));
            for (int i = 1; i <= 8; i++)
                add(56 + i, "Cowl flaps " + i.ToString(CultureInfo.InvariantCulture));
            add(65, "Throttle vertical");
            add(66, "Throttle horizontal");
            // X-Plane 12 additions, numbered by the use-code string table of X-Plane.exe (joy_use_thro9 ...
            // joy_use_viewpoint_ud follow joy_use_thro_hrz = 66); the per-use arrays of X-Plane 12 prf files grow in
            // the same steps (74, 79, 81 entries). Not cataloged: 74/75 joy_use_rudd_lft/rgt and 76/77
            // joy_use_circ_lr/ud (meaning unverified), 79/80 joy_use_viewpoint_lr/ud (view).
            add(67, "Throttle 9");
            add(68, "Copilot pitch");
            add(69, "Copilot roll");
            add(70, "Copilot yaw");
            add(71, "Copilot left toe brake");
            add(72, "Copilot right toe brake");
            add(73, "Copilot nosewheel tiller");
            add(78, "Wheel brakes");
            return axes.OrderBy(a => a.Code).ToArray();
        }

        // Use codes whose value is centred (-1..1); every other flight axis is one-sided (0..1, joy_mapped_axis_value).
        private static readonly HashSet<int> CentredCodes = new HashSet<int> { 1, 2, 3, 17, 18, 19, 37, 47, 48, 68, 69, 70, 73 };
        // Axes the control-loaded flag is for (X-Plane adds trim on them).
        private static readonly HashSet<int> TrimmedCodes = new HashSet<int> { 1, 2, 3, 68, 69, 70 };
        private static readonly HashSet<int> ThrottleCodes = new HashSet<int> { 4, 20, 21, 22, 23, 38, 53, 54, 55, 56, 65, 66, 67 };
        private static readonly HashSet<int> PropCodes = new HashSet<int> { 8, 24, 25, 26, 27 };
        private static readonly HashSet<int> MixtureCodes = new HashSet<int> { 9, 28, 29, 30, 31 };
        private const string CurveKey = "axis_response_curve";
        private const string CurveOwner = "curve:";

        // Which axes a fixed range of the curve editor belongs to (XPlaneAxisModel.RangeTypes order).
        private static bool RangeFits(int rangeType, int code)
        {
            switch (rangeType)
            {
                case 1: case 4: case 5: case 6: return ThrottleCodes.Contains(code);
                case 2: case 7: return PropCodes.Contains(code);
                case 3: return MixtureCodes.Contains(code);
                default: return true;
            }
        }

        private static readonly string[] FlightCommandPrefixes =
        {
            "sim/flight_controls/", "sim/engines/", "sim/autopilot/", "sim/fuel/", "sim/electrical/", "sim/lights/",
            "sim/systems/", "sim/radios/", "sim/instruments/", "sim/GPS/", "sim/FMS/", "sim/FMS2/", "sim/magnetos/",
            "sim/starters/", "sim/ignition/", "sim/igniters/", "sim/bleed_air/", "sim/pressurization/", "sim/ice/",
            "sim/annunciator/", "sim/transponder/", "sim/audio_panel/", "sim/audio_panel_copilot/", "sim/weapons/",
            "sim/fadec/", "sim/oxy/", "sim/altair/", "sim/HUD/"
        };

        private sealed class XpSlot
        {
            public int Index;
            public int VendorId;
            public int ProductId;
            public string Location;
        }

        // One input the export wants to set: an axis entry (value = use code) or a button entry (value = command).
        private sealed class XpTarget
        {
            public bool IsAxis;
            public int Index;
            public string ActionId;
            public string Value;
            public bool Inverted;
            public string RelationName;
            public int Code;
            public string Device;
            public AxisTuning Tuning;
            // The profile's bind (export), for the "Only add" report.
            public GameInputBinding Binding;
        }

        private sealed class XpContext
        {
            public XPlanePrf Prf;
            public List<XpSlot> Slots = new List<XpSlot>();
            public GameDeviceDirectory Devices;
            public Dictionary<int, GameDevice> DeviceBySlot = new Dictionary<int, GameDevice>();
            public List<string> Warnings;
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            foreach (string root in DiscoverInstallRoots())
            {
                string preferences = Path.Combine(root, "Output", "preferences");
                string global = Path.Combine(preferences, "X-Plane Joystick Settings.prf");
                if (File.Exists(global))
                    paths.Add(global);
                string profiles = Path.Combine(preferences, "control profiles");
                if (Directory.Exists(profiles))
                    paths.AddRange(Directory.GetFiles(profiles, "* - Joysticks.prf").OrderByDescending(File.GetLastWriteTimeUtc));
            }
            return paths;
        }

        // The installs of this version: the user's folder (Settings > Games), else the folders X-Plane's installer
        // lists in %LOCALAPPDATA%\x-plane_install_<version>.txt and the Steam app's folder.
        private List<string> DiscoverInstallRoots()
        {
            return GameInstalls.OverrideOr(GameId, () => DetectInstallRoots(version));
        }

        public static List<string> DetectInstallRoots(int version)
        {
            List<string> roots = new List<string>();
            Action<string> add = folder =>
            {
                string root = NormalizeRoot(folder);
                if (root.Length > 0 && Directory.Exists(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    roots.Add(root);
            };
            foreach (string folder in InstallLogRoots(version))
                add(folder);
            if (Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) != "off")
            {
                foreach (string folder in GameInstalls.SteamFolders(new[] { version == 11 ? 269950 : 2014780 }, new List<string>()))
                {
                    if (File.Exists(Path.Combine(folder, "X-Plane.exe")))
                        add(folder);
                }
            }
            return roots;
        }

        // X-Plane's installer writes every install folder of a version into %LOCALAPPDATA%\x-plane_install_<version>.txt,
        // one per line (X-Plane 12 on Steam: "H:\SteamLibrary/steamapps/common/X-Plane 12/"). A version that was never
        // started (or installed by Steam without the installer) may have no file.
        public static List<string> InstallLogRoots(int version)
        {
            List<string> roots = new List<string>();
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "x-plane_install_" + version.ToString(CultureInfo.InvariantCulture) + ".txt");
            if (!File.Exists(file))
                return roots;
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    string root = NormalizeRoot(line);
                    if (root.Length > 0 && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                        roots.Add(root);
                }
            }
            catch (IOException)
            {
            }
            return roots;
        }

        private static string NormalizeRoot(string folder)
        {
            string trimmed = (folder ?? string.Empty).Trim().Trim('"').TrimEnd('\\', '/');
            if (trimmed.Length == 0)
                return string.Empty;
            try
            {
                return Path.GetFullPath(trimmed).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return trimmed;
            }
        }

        // The version an X-Plane folder belongs to (11 or 12; 0 when it cannot be told): listed in an installer file,
        // else the Steam folder name ("X-Plane 11" / "X-Plane 12").
        public static int VersionOfRoot(string folder)
        {
            string root = NormalizeRoot(folder);
            if (root.Length == 0)
                return 0;
            foreach (int candidate in new[] { 12, 11 })
            {
                if (InstallLogRoots(candidate).Contains(root, StringComparer.OrdinalIgnoreCase))
                    return candidate;
            }
            string name = Path.GetFileName(root);
            if (Regex.IsMatch(name, @"\b11\b") || name.EndsWith("11", StringComparison.Ordinal))
                return 11;
            if (Regex.IsMatch(name, @"\b12\b") || name.EndsWith("12", StringComparison.Ordinal))
                return 12;
            return 0;
        }

        // Commands.txt of the first install of this version (null when none is found).
        public string CommandsFile()
        {
            return DiscoverInstallRoots().Select(r => Path.Combine(r, "Resources", "plugins", "Commands.txt")).FirstOrDefault(File.Exists);
        }

        private static bool TryOpen(string path, GameDeviceDirectory devices, List<string> errors, List<string> warnings, out XpContext context)
        {
            context = null;
            XPlanePrf prf;
            if (!XPlanePrf.TryLoad(path, errors, out prf))
                return false;
            if (prf.Get("_joy_AXIS_use0") == null && prf.Get("_joy_BUTN_use0") == null)
            {
                errors.Add("The file has no joystick assignments; pick the '... Joysticks.prf' file (not Keys.prf): " + path);
                return false;
            }

            context = new XpContext { Prf = prf, Devices = devices, Warnings = warnings };
            for (int slot = 0; slot < SlotCount; slot++)
            {
                Match match = UniqueId.Match((prf.Get("_joy_unique_id" + slot.ToString(CultureInfo.InvariantCulture)) ?? string.Empty).Trim());
                int vid;
                int pid;
                if (!match.Success ||
                    !int.TryParse(match.Groups["vid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out vid) ||
                    !int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                    continue;
                context.Slots.Add(new XpSlot
                {
                    Index = slot,
                    VendorId = vid,
                    ProductId = pid,
                    Location = (prf.Get("_joy_location" + slot.ToString(CultureInfo.InvariantCulture)) ?? string.Empty).Trim()
                });
            }
            return true;
        }

        // The connected device of a slot. A VID:PID that is unique on both sides is enough; identical devices are told
        // apart by the HID interface path X-Plane stores in _joy_location.
        private static GameDevice DeviceForSlot(XpContext context, XpSlot slot, out string problem)
        {
            problem = string.Empty;
            GameDevice cached;
            if (context.DeviceBySlot.TryGetValue(slot.Index, out cached))
                return cached;

            List<GameDevice> candidates = context.Devices == null ? new List<GameDevice>() : context.Devices.FindByVidPid(slot.VendorId, slot.ProductId);
            bool sharedSlot = context.Slots.Count(s => s.VendorId == slot.VendorId && s.ProductId == slot.ProductId) > 1;
            GameDevice device = null;
            if (candidates.Count == 0)
                problem = "is not connected";
            else if (candidates.Count == 1 && !sharedSlot)
                device = candidates[0];
            else
            {
                foreach (GameDevice candidate in candidates)
                    context.Devices.EnsureCapabilities(candidate, context.Warnings);
                device = candidates.FirstOrDefault(c => c.HidPath.Length > 0 && string.Equals(c.HidPath, slot.Location, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                    problem = "matches several identical devices and none has the stored HID path (USB port changed?)";
            }

            if (device != null && !context.Devices.EnsureCapabilities(device, context.Warnings))
            {
                problem = "could not be read";
                device = null;
            }
            if (device != null)
                context.DeviceBySlot[slot.Index] = device;
            return device;
        }

        private static XpSlot SlotForDevice(XpContext context, string joyProDevice, out GameDevice device, out string reason)
        {
            reason = string.Empty;
            device = context.Devices == null ? null : context.Devices.FindByJoyProName(joyProDevice);
            if (device == null || !device.HasVidPid)
            {
                reason = "device '" + joyProDevice + "' is not connected.";
                return null;
            }

            GameDevice wanted = device;
            List<XpSlot> slots = context.Slots.Where(s => s.VendorId == wanted.VendorId && s.ProductId == wanted.ProductId).ToList();
            foreach (XpSlot slot in slots)
            {
                string problem;
                if (DeviceForSlot(context, slot, out problem) == wanted)
                    return slot;
            }

            reason = slots.Count == 0
                ? "X-Plane has no slot for '" + joyProDevice + "' yet; start X-Plane once with the device connected."
                : "the X-Plane slot of '" + joyProDevice + "' could not be told apart from an identical device.";
            return null;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            XpContext context;
            if (!TryOpen(configPath, devices, result.Errors, result.Warnings, out context))
                return result;

            HashSet<int> reported = new HashSet<int>();
            foreach (XpSlot slot in context.Slots)
            {
                string problem;
                GameDevice device = DeviceForSlot(context, slot, out problem);
                if (device == null)
                {
                    if (reported.Add(slot.Index) && HasFlightAssignments(context, slot.Index, catalog))
                        result.Warnings.Add("X-Plane slot " + slot.Index.ToString(CultureInfo.InvariantCulture) + " (" + GameDeviceDirectory.VidPidKey(slot.VendorId, slot.ProductId) + ") " + problem + "; its binds were not imported.");
                    continue;
                }

                for (int local = 0; local < Math.Min(AxesPerSlot, device.AxisSlots.Count); local++)
                {
                    int index = slot.Index * AxesPerSlot + local;
                    XpAxis axis = AxisByCode(AxisUse(context, index));
                    if (axis == null || !catalog.Contains(axis.Id))
                        continue;
                    bool inverted = (context.Prf.Get("_joy_rev_axis" + index.ToString(CultureInfo.InvariantCulture)) ?? "0").Trim() == "1";
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = axis.Id,
                        Device = device.JoyProName,
                        Input = GameInputTokens.AxisToken(device.AxisSlots[local]),
                        IsAxis = true,
                        Inverted = inverted,
                        Tuning = ReadTuning(context, index, axis, inverted, result)
                    });
                }

                for (int local = 0; local < ButtonsPerSlot; local++)
                {
                    int index = slot.Index * ButtonsPerSlot + local;
                    string command = ButtonUse(context, index);
                    GameAction action = catalog.Find(command);
                    string token = ButtonToken(device, local);
                    if (action == null || action.IsAxis || token.Length == 0)
                        continue;
                    result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = device.JoyProName, Input = token, IsAxis = false });
                }
            }
            return result;
        }

        // The axis's response curve as the relation's X-Plane tuning: the exact stops, interpolation and fixed range;
        // the 11-point user curve gets the curve resampled (what the GUI's curve editor shows). Without a curve line
        // the tuning only holds reverse and one-sided, so the import keeps the base.
        private static AxisTuning ReadTuning(XpContext context, int index, XpAxis axis, bool inverted, GameIoResult result)
        {
            AxisTuning tuning = new AxisTuning { Inverted = inverted, Slider = !CentredCodes.Contains(axis.Code) };
            string text = context.Prf.Get(CurveKey + index.ToString(CultureInfo.InvariantCulture));
            if (text == null)
                return tuning;
            XPlaneResponseCurve curve;
            int mode = -1;
            int range = 0;
            if (XPlaneResponseCurve.TryParse(text, out curve))
            {
                mode = Array.IndexOf(XPlaneAxisModel.InterpModes, curve.Mode);
                range = curve.RangeType.Length == 0 ? 0 : Array.IndexOf(XPlaneAxisModel.RangeTypes, curve.RangeType);
            }
            if (curve == null || mode < 0 || range < 0)
            {
                result.Warnings.Add("X-Plane axis " + index.ToString(CultureInfo.InvariantCulture) + " (" + axis.Title + ") has a response curve JoyPro cannot read; it stays in the file as it is: " + text.Trim());
                return tuning;
            }
            tuning.SetControlPoints(curve.Points);
            tuning.Native["interp"] = mode;
            if (range > 0)
            {
                tuning.Native["rangeType"] = range;
                for (int i = 0; i < 3; i++)
                    tuning.Native["rangePt" + i.ToString(CultureInfo.InvariantCulture)] = curve.RangePoints[i];
            }
            List<KeyValuePair<double, double>> points = tuning.ControlPoints();
            tuning.Curvature = Enumerable.Range(0, AxisModels.UserCurvePoints)
                .Select(i => Math.Round(XPlaneAxisModel.Interpolate(points, mode, i / (double)(AxisModels.UserCurvePoints - 1)), 4)).ToList();
            result.Steps.Add("Response curve of " + axis.Title + " (axis " + index.ToString(CultureInfo.InvariantCulture) + "): " + curve.Points.Count.ToString(CultureInfo.InvariantCulture) + " stops, " + curve.Mode +
                (range > 0 ? ", " + curve.RangeType : string.Empty) + ".");
            return tuning;
        }

        private static bool HasFlightAssignments(XpContext context, int slot, GameCatalog catalog)
        {
            for (int local = 0; local < AxesPerSlot; local++)
            {
                XpAxis axis = AxisByCode(AxisUse(context, slot * AxesPerSlot + local));
                if (axis != null && catalog.Contains(axis.Id))
                    return true;
            }
            for (int local = 0; local < ButtonsPerSlot; local++)
            {
                if (catalog.Contains(ButtonUse(context, slot * ButtonsPerSlot + local)))
                    return true;
            }
            return false;
        }

        // Device buttons first, then the first hat as 8 buttons clockwise from up.
        private static string ButtonToken(GameDevice device, int local)
        {
            if (local < device.ButtonCount)
                return GameInputTokens.ButtonToken(local);
            int direction = local - device.ButtonCount;
            return device.PovCount > 0 && direction < HatButtons ? GameInputTokens.PovToken(0, direction) : string.Empty;
        }

        private static int AxisUse(XpContext context, int index)
        {
            int code;
            return int.TryParse((context.Prf.Get("_joy_AXIS_use" + index.ToString(CultureInfo.InvariantCulture)) ?? "0").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code) ? code : 0;
        }

        private static string ButtonUse(XpContext context, int index)
        {
            return (context.Prf.Get("_joy_BUTN_use" + index.ToString(CultureInfo.InvariantCulture)) ?? NoCommand).Trim();
        }

        private static XpAxis AxisByCode(int code)
        {
            return Axes.FirstOrDefault(a => a.Code == code);
        }

        private static XpAxis AxisById(string id)
        {
            return Axes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static string Encode(XpContext context, GameInputBinding binding, GameAction action, out XpTarget target)
        {
            target = null;
            if (binding.IsKeyboard)
                return "X-Plane keyboard shortcuts (Keys.prf) are not written by JoyPro.";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "X-Plane joystick inputs cannot have modifiers.";

            GameDevice device;
            string reason;
            XpSlot slot = SlotForDevice(context, binding.Device, out device, out reason);
            if (slot == null)
                return reason;

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";

            if (action.IsAxis)
            {
                XpAxis axis = AxisById(action.Id);
                if (axis == null)
                    return "unknown X-Plane axis '" + action.Id + "'.";
                if (input.Kind != JoyInputKind.Axis)
                    return "the action needs an axis.";
                int local = device.AxisSlots.IndexOf(input.Index);
                if (local < 0 || local >= AxesPerSlot)
                    return "the device has no " + binding.Input + " axis.";
                target = new XpTarget { IsAxis = true, Index = slot.Index * AxesPerSlot + local, ActionId = axis.Id, Value = axis.Code.ToString(CultureInfo.InvariantCulture), Inverted = binding.Inverted, RelationName = binding.RelationName,
                    Code = axis.Code, Device = binding.Device, Tuning = binding.Tuning, Binding = binding };
                return string.Empty;
            }

            int button;
            if (input.Kind == JoyInputKind.Button)
            {
                if (input.Index >= device.ButtonCount)
                    return "the device has only " + device.ButtonCount.ToString(CultureInfo.InvariantCulture) + " buttons.";
                button = input.Index;
            }
            else if (input.Kind == JoyInputKind.Pov)
            {
                if (input.Index != 0 || device.PovCount == 0)
                    return "X-Plane only maps the first hat of a device to buttons.";
                button = device.ButtonCount + input.Direction;
            }
            else
            {
                return "'" + binding.Input + "' is an axis; bind it to an axis action.";
            }
            if (button >= ButtonsPerSlot)
                return "X-Plane only keeps " + ButtonsPerSlot.ToString(CultureInfo.InvariantCulture) + " buttons per device.";
            target = new XpTarget { IsAxis = false, Index = slot.Index * ButtonsPerSlot + button, ActionId = action.Id, Value = action.Id, RelationName = binding.RelationName, Binding = binding };
            return string.Empty;
        }

        private static string OwnedToken(XpTarget target)
        {
            return (target.IsAxis ? "A" : "B") + target.Index.ToString(CultureInfo.InvariantCulture);
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            XpContext context;
            if (!TryOpen(request.ConfigPath, request.Devices, result.Errors, result.Warnings, out context))
                return result;

            Dictionary<string, XpTarget> wanted = new Dictionary<string, XpTarget>(StringComparer.Ordinal);
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                XpTarget target = null;
                string reason = action == null ? "not in the " + DisplayName + " catalog." : Encode(context, binding, action, out target);
                XpTarget existing;
                if (reason.Length == 0 && wanted.TryGetValue(OwnedToken(target), out existing))
                    reason = "the input is already used by relation '" + existing.RelationName + "'.";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                wanted[OwnedToken(target)] = target;
            }

            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
            Dictionary<string, HashSet<string>> ownedBefore = owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            List<XpTarget> writtenAxes = new List<XpTarget>();
            bool replaceOwned = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            if (replaceOwned)
            {
                // "My profile wins": every axis use and command the profile binds loses its other inputs on connected
                // devices, so a moved bind fully moves. Inputs JoyPro wrote before count like any other bind.
                ReleaseOtherInputs(context, wanted, result);
                owned.Clear();
            }
            if (request.Mode == GameExportMode.Clean || request.NukeUnusedDevices)
                ReleaseDevices(context, wanted, request.Mode == GameExportMode.Clean, request.NukeUnusedDevices);

            foreach (XpTarget target in wanted.Values.OrderBy(t => t.IsAxis ? 0 : 1).ThenBy(t => t.Index))
            {
                string current = target.IsAxis ? AxisUse(context, target.Index).ToString(CultureInfo.InvariantCulture) : ButtonUse(context, target.Index);
                bool free = target.IsAxis ? current == "0" : current == NoCommand;
                if (current != target.Value && !free)
                {
                    // "Only add" never replaces an assignment: the input keeps the game's use and the profile's bind is
                    // reported (X-Plane has one use per axis and one command per button).
                    if (request.Mode == GameExportMode.Add)
                    {
                        result.BindingsSkipped++;
                        result.SkippedTakenInputs.Add(SkippedMultiBind(target, Describe(target.IsAxis, current)));
                        continue;
                    }
                    // Merge and "my profile wins": the game's other use on the profile's input is a conflict and goes.
                    result.Warnings.Add("Relation '" + target.RelationName + "' (" + target.ActionId + ") replaced '" + Describe(target.IsAxis, current) + "' on its input.");
                }
                // "Only add": the action already on its input is nothing to do (its curve stays the game's too).
                if (request.Mode == GameExportMode.Add && current == target.Value)
                    continue;
                if (target.IsAxis)
                {
                    // X-Plane ignores an axis use that two connected axes share, so the use cannot sit on the old and
                    // the new axis after a move: "only add" leaves it where the game has it, merge moves it.
                    List<int> others = OtherAxesWithUse(context, target, wanted);
                    if (others.Count > 0 && request.Mode == GameExportMode.Add)
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + target.RelationName + "' (" + target.ActionId + "): X-Plane already has this axis use on axis " + string.Join(", ", others) + " and ignores a use two axes share; add keeps it.");
                        continue;
                    }
                    foreach (int other in others)
                    {
                        Assign(context, true, other, null, false);
                        context.Prf.Remove(CurveKey + other.ToString(CultureInfo.InvariantCulture));
                        result.Warnings.Add("Relation '" + target.RelationName + "' (" + target.ActionId + "): moved from axis " + other.ToString(CultureInfo.InvariantCulture) + " (X-Plane ignores an axis use two axes share).");
                    }
                }

                Assign(context, target.IsAxis, target.Index, target.Value, target.Inverted);
                GameOwnershipStore.Add(owned, target.ActionId, OwnedToken(target));
                result.BindingsWritten++;
                if (target.IsAxis)
                    writtenAxes.Add(target);
            }

            foreach (XpTarget target in writtenAxes)
                UpdateCurve(context, target, request.Mode, ownedBefore, owned, result);
            ApplySettings(context, writtenAxes, result);
            WarnSharedUses(context, writtenAxes, result);

            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + context.Prf.ChangedLines.ToString(CultureInfo.InvariantCulture) + " line(s) of the joystick preferences would change.");
                return result;
            }

            GameOwnershipStore.Save(request.ConfigPath, GameId, owned);
            if (context.Prf.ChangedLines == 0)
            {
                result.Steps.Add("The X-Plane joystick assignments already match.");
                return result;
            }

            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            context.Prf.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(context.Prf.ChangedLines.ToString(CultureInfo.InvariantCulture) + " line(s) of the joystick preferences changed.");
            return result;
        }

        // X-Plane ignores a use that two connected axes share ("Multiple axes assigned to yaw" in the joystick
        // settings, the value stays 0; checked in game, X-Plane 12.4): an exported axis whose use another connected
        // device's axis also has gets a warning (nuke unused devices, or clean, clears the other one).
        private static void WarnSharedUses(XpContext context, List<XpTarget> axes, GameIoResult result)
        {
            foreach (XpTarget target in axes)
            {
                foreach (XpSlot slot in context.Slots)
                {
                    string problem;
                    if (DeviceForSlot(context, slot, out problem) == null)
                        continue;
                    for (int index = slot.Index * AxesPerSlot; index < (slot.Index + 1) * AxesPerSlot; index++)
                    {
                        if (index == target.Index || AxisUse(context, index) != target.Code)
                            continue;
                        result.Warnings.Add("Relation '" + target.RelationName + "' (" + target.ActionId + "): axis " + index.ToString(CultureInfo.InvariantCulture)
                            + " of X-Plane slot " + slot.Index.ToString(CultureInfo.InvariantCulture) + " (" + GameDeviceDirectory.VidPidKey(slot.VendorId, slot.ProductId)
                            + ") has the same use; X-Plane ignores an axis use that two connected axes share (export with nuke unused devices or clean).");
                    }
                }
            }
        }

        private static string Describe(bool isAxis, string value)
        {
            if (!isAxis)
                return value;
            int code;
            XpAxis axis = int.TryParse(value, out code) ? AxisByCode(code) : null;
            return axis != null ? axis.Title + " axis" : "axis use " + value;
        }

        // "My profile wins": axes of connected devices with a use the profile binds on another axis, and buttons with a
        // command the profile binds on another button, go back to unassigned (an axis's response curve goes with it).
        // Slots of devices that are not connected keep their assignments.
        private static void ReleaseOtherInputs(XpContext context, Dictionary<string, XpTarget> wanted, GameIoResult result)
        {
            HashSet<int> codes = new HashSet<int>(wanted.Values.Where(t => t.IsAxis).Select(t => t.Code));
            HashSet<string> commands = new HashSet<string>(wanted.Values.Where(t => !t.IsAxis).Select(t => t.Value), StringComparer.Ordinal);
            int released = 0;
            foreach (XpSlot slot in context.Slots)
            {
                string problem;
                if (DeviceForSlot(context, slot, out problem) == null)
                    continue;
                for (int index = slot.Index * AxesPerSlot; index < (slot.Index + 1) * AxesPerSlot; index++)
                {
                    if (codes.Contains(AxisUse(context, index)) && !wanted.ContainsKey("A" + index.ToString(CultureInfo.InvariantCulture)))
                    {
                        Assign(context, true, index, null, false);
                        context.Prf.Remove(CurveKey + index.ToString(CultureInfo.InvariantCulture));
                        released++;
                    }
                }
                for (int index = slot.Index * ButtonsPerSlot; index < (slot.Index + 1) * ButtonsPerSlot; index++)
                {
                    if (commands.Contains(ButtonUse(context, index)) && !wanted.ContainsKey("B" + index.ToString(CultureInfo.InvariantCulture)))
                    {
                        Assign(context, false, index, null, false);
                        released++;
                    }
                }
            }
            if (released > 0)
                result.Steps.Add("Released " + released.ToString(CultureInfo.InvariantCulture) + " other input(s) of the profile's axes and commands.");
        }

        // Axes of connected devices (not the profile's own) that have the target's use.
        private static List<int> OtherAxesWithUse(XpContext context, XpTarget target, Dictionary<string, XpTarget> wanted)
        {
            List<int> others = new List<int>();
            foreach (XpSlot slot in context.Slots)
            {
                string problem;
                if (DeviceForSlot(context, slot, out problem) == null)
                    continue;
                for (int index = slot.Index * AxesPerSlot; index < (slot.Index + 1) * AxesPerSlot; index++)
                {
                    if (index != target.Index && AxisUse(context, index) == target.Code && !wanted.ContainsKey("A" + index.ToString(CultureInfo.InvariantCulture)))
                        others.Add(index);
                }
            }
            return others;
        }

        private ExportSkippedBind SkippedMultiBind(XpTarget target, string gameAction)
        {
            GameInputBinding binding = target.Binding ?? new GameInputBinding();
            return new ExportSkippedBind
            {
                Game = GameId,
                Device = binding.Device ?? string.Empty,
                Input = binding.Input ?? string.Empty,
                ModifiersText = binding.Reformers == null ? string.Empty : string.Join(" + ", binding.Reformers.Select(r => { string[] p = (r ?? string.Empty).Split('§'); return p.Length >= 3 ? p[1] + " " + p[2] : r; })),
                Relation = binding.RelationName ?? target.RelationName ?? string.Empty,
                Action = target.ActionId ?? string.Empty,
                GameAction = gameAction ?? string.Empty
            };
        }

        // Clean mode: every other input of a connected device that holds a cataloged flight axis or command becomes
        // unassigned. Slots of devices that are not connected (or cannot be matched) keep their assignments.
        // X-Plane is flight only, so JoyPro takes over whole devices: clean mode clears every axis and button of the
        // devices the export writes, nuke clears the connected devices it does not use. Slots of devices that are not
        // connected (or cannot be matched) keep their assignments.
        private static void ReleaseDevices(XpContext context, Dictionary<string, XpTarget> wanted, bool clean, bool nuke)
        {
            foreach (XpSlot slot in context.Slots)
            {
                string problem;
                if (DeviceForSlot(context, slot, out problem) == null)
                    continue;
                int firstAxis = slot.Index * AxesPerSlot;
                int firstButton = slot.Index * ButtonsPerSlot;
                bool used = wanted.Values.Any(t => t.IsAxis
                    ? t.Index >= firstAxis && t.Index < firstAxis + AxesPerSlot
                    : t.Index >= firstButton && t.Index < firstButton + ButtonsPerSlot);
                if (used ? !clean : !nuke)
                    continue;

                for (int index = firstAxis; index < firstAxis + AxesPerSlot; index++)
                {
                    if (AxisUse(context, index) != 0 && !wanted.ContainsKey("A" + index.ToString(CultureInfo.InvariantCulture)))
                    {
                        Assign(context, true, index, null, false);
                        // The axis's response curve goes with its assignment.
                        context.Prf.Remove(CurveKey + index.ToString(CultureInfo.InvariantCulture));
                    }
                }
                for (int index = firstButton; index < firstButton + ButtonsPerSlot; index++)
                {
                    if (ButtonUse(context, index) != NoCommand && !wanted.ContainsKey("B" + index.ToString(CultureInfo.InvariantCulture)))
                        Assign(context, false, index, null, false);
                }
            }
        }

        // The response curve JoyPro wants on an axis (null: none, the tuning is linear and has no fixed range).
        private static XPlaneResponseCurve WantedCurve(XpTarget target, GameIoResult result)
        {
            AxisTuning tuning = target.Tuning ?? new AxisTuning();
            List<KeyValuePair<double, double>> points = XPlaneAxisModel.CurvePoints(tuning);
            int range = XPlaneAxisModel.RangeType(tuning);
            if (range > 0 && !RangeFits(range, target.Code))
            {
                result.Warnings.Add("Relation '" + target.RelationName + "' (" + target.ActionId + "): the fixed range " + XPlaneAxisModel.RangeTypes[range] + " does not belong to this axis; written without it.");
                range = 0;
            }
            if (range == 0 && XPlaneAxisModel.IsIdentity(points))
                return null;
            XPlaneResponseCurve curve = new XPlaneResponseCurve
            {
                Mode = XPlaneAxisModel.InterpModes[XPlaneAxisModel.Interp(tuning)],
                Points = points.Select(p => new KeyValuePair<double, double>(Math.Round(Math.Max(0, Math.Min(1, p.Key)), 6), Math.Round(Math.Max(0, Math.Min(1, p.Value)), 6))).ToList()
            };
            if (range > 0)
            {
                curve.RangeType = XPlaneAxisModel.RangeTypes[range];
                for (int i = 0; i < 3; i++)
                    curve.RangePoints[i] = (int)Math.Round(tuning.NativeOr("rangePt" + i.ToString(CultureInfo.InvariantCulture), 0));
            }
            return curve;
        }

        // Writes the response curve of an axis JoyPro exports. A curve made in X-Plane (not imported, not JoyPro's)
        // stays when JoyPro's tuning is linear, and in merge mode; clean mode and JoyPro's own curves follow the tuning.
        private static void UpdateCurve(XpContext context, XpTarget target, GameExportMode mode, Dictionary<string, HashSet<string>> ownedBefore,
            Dictionary<string, HashSet<string>> owned, GameIoResult result)
        {
            string suffix = target.Index.ToString(CultureInfo.InvariantCulture);
            string key = CurveKey + suffix;
            string ownerKey = CurveOwner + suffix;
            string existingText = context.Prf.Get(key);
            XPlaneResponseCurve existing = null;
            bool parsed = existingText != null && XPlaneResponseCurve.TryParse(existingText, out existing);
            bool ours = existingText != null && GameOwnershipStore.Contains(ownedBefore, ownerKey, CurveHash(existingText));
            XPlaneResponseCurve wanted = WantedCurve(target, result);
            owned.Remove(ownerKey);
            string label = "Relation '" + target.RelationName + "' (" + target.ActionId + ")";
            if (wanted == null)
            {
                if (existingText == null)
                    return;
                if ((ours && mode != GameExportMode.Add) || mode == GameExportMode.Clean)
                {
                    context.Prf.Remove(key);
                    result.Steps.Add(label + ": response curve removed (linear).");
                }
                else
                    result.Warnings.Add(label + ": the axis has a response curve made in X-Plane; JoyPro's tuning is linear, so it stays (import it, or export with clean, to manage it).");
                return;
            }
            bool same = parsed && existing.SameAs(wanted);
            if (existingText != null && !ours && !same)
            {
                if (mode == GameExportMode.Merge || mode == GameExportMode.Add)
                {
                    result.Warnings.Add(label + ": the axis has a response curve made in X-Plane; " + (mode == GameExportMode.Add ? "add" : "merge") + " keeps it.");
                    return;
                }
                result.Warnings.Add(label + " replaced the response curve made in X-Plane: " + existingText.Trim());
            }
            if (!same)
            {
                context.Prf.Set(key, wanted.Render());
                result.Steps.Add(label + ": response curve " + wanted.Mode + ", " + wanted.Points.Count.ToString(CultureInfo.InvariantCulture) + " stops" + (wanted.RangeType.Length > 0 ? ", " + wanted.RangeType : string.Empty) + ".");
            }
            GameOwnershipStore.Add(owned, ownerKey, CurveHash(context.Prf.Get(key)));
        }

        // Control settings (engine ffb set --game XPlane12|XPlane11): game-wide ones on device *, the control-loaded
        // flag per device on the pitch/roll/yaw axes JoyPro exports. Each version's model only has its own settings
        // (X-Plane 11: stability augmentation; X-Plane 12: copilot response, control-loaded axes), and only lines the
        // file already has are changed.
        // The game-wide control settings the prf holds (engine sync compares them with the profile's).
        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            XPlanePrf prf;
            if (!XPlanePrf.TryLoad(configPath, errors, out prf))
            {
                warnings?.AddRange(errors);
                return values;
            }
            foreach (KeyValuePair<string, string> key in XPlaneFfbModel.GameWideKeys)
            {
                if (FfbSettings.Parameter(GameId, key.Key) == null)
                    continue;
                double value;
                string text = prf.Get(key.Value);
                if (text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    values[key.Key] = value;
            }
            return values;
        }

        private void ApplySettings(XpContext context, List<XpTarget> axes, GameIoResult result)
        {
            Dictionary<string, double> gameWide = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            foreach (KeyValuePair<string, double> value in gameWide.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string prfKey;
                if (!XPlaneFfbModel.GameWideKeys.TryGetValue(value.Key, out prfKey))
                    continue;
                FfbParameter parameter = FfbSettings.Parameter(GameId, value.Key);
                string rendered = parameter != null && parameter.Toggle
                    ? (value.Value != 0 ? "1" : "0")
                    : Math.Max(0, Math.Min(1, value.Value)).ToString("0.000000", CultureInfo.InvariantCulture);
                SetExisting(context, prfKey, rendered, value.Key, result);
            }
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XpTarget target in axes.Where(a => TrimmedCodes.Contains(a.Code)))
            {
                double loaded;
                if (!FfbSettings.Resolve(InternalDataManagement.FfbOverrides, target.Device, GameId, null).TryGetValue(XPlaneFfbModel.ControlLoaded, out loaded))
                    continue;
                SetExisting(context, "_joy_ffb_axis" + target.Index.ToString(CultureInfo.InvariantCulture), loaded != 0 ? "1" : "0", XPlaneFfbModel.ControlLoaded + " (" + target.ActionId + ")", result);
            }
        }

        private static void SetExisting(XpContext context, string prfKey, string value, string setting, GameIoResult result)
        {
            string current = context.Prf.Get(prfKey);
            if (current == null)
            {
                result.Warnings.Add("X-Plane setting " + setting + " not written: the file has no " + prfKey + " line (another X-Plane version).");
                return;
            }
            if (current.Trim() == value)
                return;
            context.Prf.Set(prfKey, value);
            result.Steps.Add("Setting " + setting + ": " + prfKey + " " + value + ".");
        }

        // Ownership token of a curve line (FNV-1a 64 of its trimmed text): JoyPro's curve while the line is unchanged.
        private static string CurveHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in (text ?? string.Empty).Trim())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return "h" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // value null = unassign. Axes also get their reverse flag; a changed button loses its stale description line.
        private static void Assign(XpContext context, bool isAxis, int index, string value, bool inverted)
        {
            string suffix = index.ToString(CultureInfo.InvariantCulture);
            if (isAxis)
            {
                context.Prf.Set("_joy_AXIS_use" + suffix, value ?? "0");
                context.Prf.Set("_joy_rev_axis" + suffix, inverted ? "1" : "0");
                return;
            }

            string command = value ?? NoCommand;
            if (ButtonUse(context, index) == command)
                return;
            context.Prf.Set("_joy_BUTN_use" + suffix, command);
            context.Prf.Remove("_joy_BUTN_use_desc" + suffix);
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            XpContext context;
            if (!TryOpen(configPath, devices, result.Errors, result.Warnings, out context))
                return;
            GameCatalog catalog = GameCatalogStore.Load(this, result.Warnings);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                GameAction action = catalog.Find(binding.ActionId);
                XpTarget target;
                if (action != null && Encode(context, binding, action, out target).Length == 0)
                {
                    GameOwnershipStore.Add(owned, target.ActionId, OwnedToken(target));
                    // An imported curve is the relation's X-Plane tuning now: JoyPro manages it.
                    string curve = target.IsAxis ? context.Prf.Get(CurveKey + target.Index.ToString(CultureInfo.InvariantCulture)) : null;
                    XPlaneResponseCurve parsed;
                    if (curve != null && XPlaneResponseCurve.TryParse(curve, out parsed) && ProfileReproduces(binding, target, parsed))
                    {
                        string ownerKey = CurveOwner + target.Index.ToString(CultureInfo.InvariantCulture);
                        owned.Remove(ownerKey);
                        GameOwnershipStore.Add(owned, ownerKey, CurveHash(curve));
                    }
                }
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
            result.FilesWritten.Add(configPath + GameOwnershipStore.Suffix);
        }

        // True when the profile's relation for this bind (after the import) exports exactly this curve: only then does
        // JoyPro take the curve over (a merge import that kept an existing relation leaves the curve the user's).
        private bool ProfileReproduces(GameInputBinding binding, XpTarget target, XPlaneResponseCurve curve)
        {
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || !relation.ISAXIS || relation.NODES == null ||
                    !relation.NODES.Any(n => n != null && GameRegistry.GameMatches(this, n.Game) && string.Equals((n.ID ?? string.Empty).Trim(), binding.ActionId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Bind bind = relation.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                if (bind == null || !string.Equals((bind.Joystick ?? string.Empty).Trim(), binding.Device, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((bind.JAxis ?? string.Empty).Trim(), binding.Input, StringComparison.OrdinalIgnoreCase))
                    continue;
                XpTarget probe = new XpTarget { Code = target.Code, ActionId = target.ActionId, RelationName = relation.NAME, Tuning = BindTuning.Resolve(bind, GameId, null).Tuning };
                XPlaneResponseCurve wanted = WantedCurve(probe, new GameIoResult());
                return wanted != null && wanted.SameAs(curve);
            }
            return false;
        }

        // Axes come from the fixed use-code table, commands from <X-Plane>\Resources\plugins\Commands.txt
        // ("<command>   <description>"), limited to cockpit/flight groups.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string commandsFile = FindCommandsFile(sourcePath);
            if (commandsFile == null)
            {
                result.Errors.Add("Commands.txt was not found. Pass --path with the X-Plane folder or <X-Plane>\\Resources\\plugins\\Commands.txt.");
                return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = commandsFile };
            foreach (XpAxis axis in Axes.Where(a => a.Code <= MaxAxisCode))
                catalog.Add(new GameAction { Id = axis.Id, Title = axis.Title, IsAxis = true, Category = "Axis" });

            foreach (string line in File.ReadAllLines(commandsFile, Encoding.UTF8))
            {
                Match match = Regex.Match(line, @"^(?<id>sim/\S+)\s*(?<title>.*)$");
                if (!match.Success)
                    continue;
                string id = match.Groups["id"].Value;
                if (!FlightCommandPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                string title = match.Groups["title"].Value.Trim().TrimEnd('.').Replace('\t', ' ');
                catalog.Add(new GameAction { Id = id, Title = title.Length > 0 ? title : id, IsAxis = false, Category = id.Split('/')[1] });
            }
            result.Steps.Add("Commands read from " + commandsFile);
            return catalog;
        }

        private string FindCommandsFile(string sourcePath)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                candidates.Add(sourcePath);
                candidates.Add(Path.Combine(sourcePath, "Commands.txt"));
                candidates.Add(Path.Combine(sourcePath, "Resources", "plugins", "Commands.txt"));
            }
            else
            {
                candidates.AddRange(DiscoverInstallRoots().Select(r => Path.Combine(r, "Resources", "plugins", "Commands.txt")));
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
