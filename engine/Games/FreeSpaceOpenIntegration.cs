using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games.FreeSpaceOpen;

namespace JoyPro.Engine.Games
{
    // A FreeSpace Open mod (docs/games/fate-of-the-galaxy.md): the game id JoyPro gives it, the Knossos mod id and the
    // name of the controls preset JoyPro writes. Other FSO mods (FreeSpace 2 with the MediaVPs, other total conversions)
    // could use the same backend with a line of their own.
    internal sealed class FsoMod
    {
        public string GameId;
        public string DisplayName;
        public string ShortName;
        public string ModId;
        public string[] Aliases;
        public string Preset;

        public static readonly FsoMod FateOfTheGalaxy = new FsoMod
        {
            GameId = "FateOfTheGalaxy",
            DisplayName = "Fate of the Galaxy (experimental)",
            ShortName = "Fate of the Galaxy",
            ModId = "FotG_2",
            Aliases = new[] { "fotg", "fateofthegalaxy", "fate" },
            Preset = "JoyPro_FotG"
        };
    }

    // FreeSpace Open 26 (SDL3 builds included) through a Knossos mod. FSO keeps the controls in a preset file,
    // <user data>\data\players\presets\<name>.json, and the pilot (data\players\<callsign>.json, PLR 4) and its campaign
    // saves (<callsign>.<campaign>.csg, CSG 7+) only name the preset; the mod's controlconfigdefaults.tbl gives the default
    // preset, the menu texts and which controls the mod switched off. Devices are Joy-0..Joy-3: the mod's settings
    // ([Input] Joystick / Joystick1..3 = SDL GUID + device index) pick them. Joysticks: 128 buttons, the first hat in four
    // directions (buttons 128..131), 8 axes counted over the axes the device has. See docs/games/fate-of-the-galaxy.md.
    internal sealed class FreeSpaceOpenIntegration : IGameIntegration, IGameConfigPathResolver, IGameSettingsReader, IGameSettingsSource,
        IGameSettingsExport, IGameBuiltInCatalog, IGameDeviceLimit, IGameScansConfigFolder
    {
        private readonly FsoMod mod;

        public FreeSpaceOpenIntegration(FsoMod mod)
        {
            this.mod = mod;
        }

        public FsoMod Mod { get { return mod; } }
        public string GameId { get { return mod.GameId; } }
        public string DisplayName { get { return mod.DisplayName; } }
        public IReadOnlyList<string> Aliases { get { return mod.Aliases; } }
        public string DbFolder { get { return mod.GameId; } }
        public bool Experimental { get { return true; } }
        // Space flight only: every control of FSO is a flight control, so JoyPro takes over the devices it writes.
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Close " + mod.ShortName + " before exporting. JoyPro writes the controls preset '" + mod.Preset + "' and selects it for your pilot (create a pilot first; " +
                       "without one select the preset in the game's Controls menu). FSO uses four devices (Options: Device 1-4), 128 buttons and the first hat (4 directions) per device.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return FsoProcessNames; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Keys with Alt and Shift (FSO's preset names know no Ctrl); joystick modifiers do not exist in FSO.
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }

        // IGameDeviceLimit: FSO reads four joysticks (CID_JOY0..3).
        public int MaxJoysticks { get { return FsoNames.Joysticks; } }
        public string SuggestedMode { get { return "virtual"; } }
        public string DeviceLimitNote { get { return "FreeSpace Open uses four devices at once (Device 1-4)."; } }
        public IReadOnlyList<string> AvoidVJoyAxes { get { return new string[0]; } }

        // fs2_open_<version>[_sdl3]_x64_<SSE2|AVX|AVX2>[-FASTDBG] of the 26 builds Knossos installs.
        private static readonly string[] FsoProcessNames = BuildProcessNames();

        private static string[] BuildProcessNames()
        {
            List<string> names = new List<string>();
            foreach (string version in new[] { "26_0_1", "26_0_0", "25_0_0", "24_2_0" })
                foreach (string sdl in new[] { string.Empty, "_sdl3" })
                    foreach (string simd in new[] { "SSE2", "AVX", "AVX2" })
                        foreach (string debug in new[] { string.Empty, "-FASTDBG" })
                            names.Add("fs2_open_" + version + sdl + "_x64_" + simd + debug);
            return names.ToArray();
        }

        // ---- where ----

        // The mod folder of this game: the user's or detected installation, else one in the Knossos library next to the
        // user data (portable).
        private string ModFolderFor(string userData)
        {
            GameInstall install = GameInstalls.Find(GameId, new List<string>());
            if (install != null && install.Installed && File.Exists(Path.Combine(install.InstallPath ?? string.Empty, "mod.json")))
                return install.InstallPath;
            try
            {
                DirectoryInfo dir = string.IsNullOrEmpty(userData) ? null : new DirectoryInfo(userData);
                if (dir != null && dir.Parent != null && dir.Parent.Parent != null &&
                    string.Equals(dir.Name, "FreeSpaceOpen", StringComparison.OrdinalIgnoreCase) && string.Equals(dir.Parent.Name, "HardLightProductions", StringComparison.OrdinalIgnoreCase))
                {
                    string found = FsoInstall.ModFoldersIn(Path.Combine(dir.Parent.Parent.FullName, "Library"), mod.ModId).FirstOrDefault();
                    if (found != null)
                        return found;
                }
            }
            catch (Exception)
            {
            }
            return Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) == "off" ? null : FsoInstall.ModFolders(mod.ModId, new List<string>()).FirstOrDefault();
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> modFolders = new List<string>();
            GameInstall install = GameInstalls.Find(GameId, warnings);
            if (install != null && install.Installed && !string.IsNullOrEmpty(install.InstallPath))
                modFolders.Add(install.InstallPath);
            bool detect = Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) != "off";
            if (detect)
                modFolders.AddRange(FsoInstall.ModFolders(mod.ModId, warnings));
            List<string> paths = new List<string>();
            IEnumerable<string> userData = detect ? FsoInstall.UserDataFolders(modFolders) : modFolders.Select(FsoInstall.UserDataFor).Where(Directory.Exists);
            foreach (string folder in userData.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<string> pilots = FsoInstall.Pilots(folder);
                if (pilots.Count > 0)
                    paths.AddRange(pilots);
                else if (File.Exists(Path.Combine(folder, "data", FsoInstall.SettingsIniName(modFolders.FirstOrDefault() ?? mod.ModId))))
                    paths.Add(PresetPath(folder, mod.Preset));
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A folder given as the config (the user data, its players folder, a Knossos or mod folder) -> the last player's
        // pilot, else the preset JoyPro writes.
        public string ResolveConfigPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return path;
            string userData = FsoInstall.UserDataOf(path);
            if (userData.Length == 0 || !Directory.Exists(Path.Combine(userData, "data")))
            {
                string modFolder = FsoInstall.ResolveModFolder(path, mod.ModId);
                userData = modFolder == null ? string.Empty : FsoInstall.UserDataFor(modFolder);
            }
            if (userData.Length == 0 && Directory.Exists(Path.Combine(path, "data", "players")))
                userData = path;
            if (userData.Length == 0)
                return path;
            string pilot = FsoInstall.Pilots(userData).FirstOrDefault();
            return pilot ?? PresetPath(userData, mod.Preset);
        }

        private static string PresetPath(string userData, string name)
        {
            return Path.Combine(userData, "data", "players", "presets", name + ".json");
        }

        private static bool IsPreset(string configPath)
        {
            string parent = Path.GetFileName(Path.GetDirectoryName(configPath ?? string.Empty) ?? string.Empty);
            return string.Equals(parent, "presets", StringComparison.OrdinalIgnoreCase);
        }

        // presets\<name>.json as FSO finds it (case does not matter on Windows).
        private static string FindPreset(string userData, string name)
        {
            string folder = Path.Combine(userData, "data", "players", "presets");
            if (!Directory.Exists(folder) || string.IsNullOrEmpty(name))
                return null;
            return Directory.GetFiles(folder, "*.json").FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
        }

        private string SettingsIniPath(string userData, string modFolder)
        {
            return Path.Combine(userData, "data", FsoInstall.SettingsIniName(string.IsNullOrEmpty(modFolder) ? mod.ModId : modFolder));
        }

        // ---- the controls the game uses now ----

        private sealed class GameState
        {
            public string UserData = string.Empty;
            public string ModFolder;
            public FsoModControls Controls;
            public FsoPilot Pilot;
            public string PresetName = string.Empty;
            public string PresetFile;
            public FsoBind[][] Bindings;
            public string Source = string.Empty;
        }

        private GameState LoadState(string configPath, List<string> warnings, List<string> errors)
        {
            GameState state = new GameState();
            state.UserData = FsoInstall.UserDataOf(configPath);
            state.ModFolder = ModFolderFor(state.UserData);
            state.Controls = FsoModControls.Load(state.ModFolder, warnings);
            if (state.ModFolder == null)
                warnings.Add(mod.ShortName + " was not found: FreeSpace Open's own defaults stand in for the mod's (set its folder in Settings > Games, or engine games install set " + GameId + " <folder>).");
            if (IsPreset(configPath))
            {
                state.PresetName = Path.GetFileNameWithoutExtension(configPath);
                if (File.Exists(configPath))
                {
                    state.PresetFile = configPath;
                    state.Bindings = FsoPresetFile.Read(configPath, errors);
                    state.Source = "preset file " + configPath;
                }
                else
                {
                    state.Bindings = Clone(state.Controls.DefaultPreset);
                    state.Source = "the mod's default controls (no pilot and no preset yet)";
                }
                return state;
            }
            if (!File.Exists(configPath))
            {
                errors.Add(mod.ShortName + " pilot not found: " + configPath + ". Start the game once and create a pilot (or pass the user data folder).");
                return state;
            }
            state.Pilot = FsoPilot.Read(configPath, errors);
            if (state.Pilot == null)
                return state;
            if (state.Pilot.Version < 4)
                warnings.Add("The pilot " + state.Pilot.Callsign + " is an old pilot file (version " + state.Pilot.Version.ToString(CultureInfo.InvariantCulture) + "); FSO converts it to a preset when it loads it. Start the game once with this pilot.");
            state.PresetName = string.IsNullOrEmpty(state.Pilot.Preset) ? "default" : state.Pilot.Preset;
            string file = state.PresetName == "default" ? null : FindPreset(state.UserData, state.PresetName);
            FsoBind[][] tablePreset;
            if (file != null)
            {
                state.PresetFile = file;
                state.Bindings = FsoPresetFile.Read(file, errors);
                state.Source = "preset '" + state.PresetName + "' (" + file + ")";
            }
            else if (state.Controls.Presets.TryGetValue(state.PresetName, out tablePreset))
            {
                state.Bindings = Clone(tablePreset);
                state.Source = state.PresetName == "default" ? "the mod's default controls" : "the mod's preset '" + state.PresetName + "'";
            }
            else
            {
                warnings.Add("The pilot uses preset '" + state.PresetName + "', which FSO does not find either; it starts with the default controls.");
                state.Bindings = Clone(state.Controls.DefaultPreset);
                state.Source = "the mod's default controls";
            }
            return state;
        }

        private static FsoBind[][] Clone(FsoBind[][] bindings)
        {
            return bindings.Select(b => new[] { b[0], b[1] }).ToArray();
        }

        // ---- devices ----

        // Joy-0..3 of the user data: [Input] Joystick, Joystick1..3 of the mod's settings, else fs2_open.ini [Default]
        // Joy0GUID / Joy0ID ... (CurrentJoystickGUID for Joy-0), which FSO uses without the in-game options.
        private List<FsoSlot> LoadSlots(string userData, string modFolder, List<string> warnings)
        {
            List<FsoSlot> slots = new List<FsoSlot>();
            FsoIni settings = FsoIni.Load(SettingsIniPath(userData, modFolder));
            for (int i = 0; i < FsoNames.Joysticks; i++)
            {
                FsoSlot slot = FsoSlot.Parse(i, settings.Get("Input", i == 0 ? "Joystick" : "Joystick" + i.ToString(CultureInfo.InvariantCulture)));
                if (slot != null && slot.Guid.Length == 32)
                    slots.Add(slot);
            }
            if (slots.Count > 0)
                return slots;
            FsoIni main = FsoIni.Load(Path.Combine(userData, "fs2_open.ini"));
            for (int i = 0; i < FsoNames.Joysticks; i++)
            {
                string n = i.ToString(CultureInfo.InvariantCulture);
                string guid = main.Get("Default", "Joy" + n + "GUID") ?? (i == 0 ? main.Get("Default", "CurrentJoystickGUID") : null);
                string id = main.Get("Default", "Joy" + n + "ID") ?? (i == 0 ? main.Get("Default", "CurrentJoystick") : null);
                int index;
                if (!string.IsNullOrEmpty(guid) && guid.Trim().Length == 32)
                    slots.Add(new FsoSlot { Index = i, Guid = guid.Trim().ToUpperInvariant(), Id = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) ? index : -1 });
            }
            return slots;
        }

        // Slot -> connected device: the same USB vendor and product id (the GUID's bytes 4-5 and 8-9); several such devices
        // are told apart by the CRC of the name, then by the device index FSO saved (unverified for identical sticks).
        private static Dictionary<int, GameDevice> MapSlots(List<FsoSlot> slots, GameDeviceDirectory devices)
        {
            Dictionary<int, GameDevice> map = new Dictionary<int, GameDevice>();
            HashSet<GameDevice> taken = new HashSet<GameDevice>();
            foreach (FsoSlot slot in slots.OrderBy(s => s.Index))
            {
                List<GameDevice> same = (devices == null ? new List<GameDevice>() : devices.Devices)
                    .Where(d => d.HasVidPid && d.VendorId == slot.Vendor && d.ProductId == slot.Product && !taken.Contains(d)).ToList();
                if (same.Count > 1)
                {
                    // A device with several HID interfaces (a keyboard with an analog joystick part) or two identical
                    // sticks: the interface with axes or buttons, then the name CRC.
                    foreach (GameDevice d in same)
                        devices.EnsureCapabilities(d, new List<string>());
                    List<GameDevice> usable = same.Where(d => !d.CapabilitiesKnown || d.AxisSlots.Count > 0 || d.ButtonCount > 0 || d.PovCount > 0).ToList();
                    if (usable.Count > 0)
                        same = usable;
                    List<GameDevice> byName = same.Where(d => slot.Crc != 0 && FsoSlot.Crc16(SdlName(d, devices)) == slot.Crc).ToList();
                    if (byName.Count > 0)
                        same = byName;
                }
                GameDevice device = same.Count > 1 ? same.OrderBy(d => d.EnumerationIndex == slot.Id ? 0 : 1).ThenBy(d => d.EnumerationIndex).First() : same.FirstOrDefault();
                if (device != null)
                {
                    map[slot.Index] = device;
                    taken.Add(device);
                }
            }
            return map;
        }

        // The name SDL takes the GUID's CRC of: "<HID manufacturer> <HID product>" as the device reports them (checked on
        // the four slots Fate of the Galaxy wrote on a real PC: "Wooting Wooting Two HE (ARM)", "VPforce Rhino FFB
        // Joystick"), the product alone without a manufacturer string.
        internal static string SdlName(GameDevice device, GameDeviceDirectory devices)
        {
            if (device == null)
                return string.Empty;
            if (devices != null && !device.CapabilitiesKnown)
                devices.EnsureCapabilities(device, new List<string>());
            string product = !string.IsNullOrEmpty(device.HidProductString) ? device.HidProductString : (device.ProductName ?? string.Empty);
            string manufacturer = HidProductNames.Manufacturer(device.HidPath);
            return manufacturer.Length > 0 && product.Length > 0 ? manufacturer + " " + product : product;
        }

        // SDL's GUID of a DirectInput device as FSO stores it: USB bus 0x0003, CRC-16 of SdlName, vendor, product,
        // version 0 (as in the GUIDs Fate of the Galaxy wrote on a real PC). Used only for a free slot.
        internal static string SdlGuid(GameDevice device, GameDeviceDirectory devices)
        {
            if (device == null || !device.HasVidPid)
                return string.Empty;
            int crc = FsoSlot.Crc16(SdlName(device, devices));
            Func<int, string> le = v => (v & 0xFF).ToString("X2", CultureInfo.InvariantCulture) + ((v >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
            return le(3) + le(crc) + le(device.VendorId) + "0000" + le(device.ProductId) + "0000" + "0000" + "0000";
        }

        // ---- read ----

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            GameState state = LoadState(configPath, result.Warnings, result.Errors);
            if (result.Errors.Count > 0 || state.Bindings == null)
                return result;
            List<FsoSlot> slots = LoadSlots(state.UserData, state.ModFolder, result.Warnings);
            Dictionary<int, GameDevice> map = MapSlots(slots, devices);
            HashSet<int> reported = new HashSet<int>();
            foreach (FsoControl control in state.Controls.Controls)
            {
                if (control.Disabled || (catalog != null && catalog.Actions.Count > 0 && !catalog.Contains(control.Name)))
                    continue;
                foreach (FsoBind bind in state.Bindings[control.Id])
                {
                    if (bind.IsNone || bind.IsMouse)
                        continue;
                    if (bind.IsKeyboard)
                    {
                        GameInputBinding key = ReadKey(control, bind);
                        if (key != null)
                            result.Bindings.Add(key);
                        continue;
                    }
                    int joy = bind.Joy;
                    GameDevice device;
                    if (joy < 0 || !map.TryGetValue(joy, out device))
                    {
                        if (joy >= 0 && reported.Add(joy))
                            result.Warnings.Add("Joy-" + joy.ToString(CultureInfo.InvariantCulture) + " (Device " + (joy + 1).ToString(CultureInfo.InvariantCulture) + ") is " +
                                                (slots.Any(s => s.Index == joy) ? "a device that is not connected" : "not assigned in the game's options") + "; its binds are not imported.");
                        continue;
                    }
                    string why;
                    string token = DecodeJoy(bind, device, devices, out why);
                    if (token.Length == 0)
                    {
                        result.Warnings.Add(control.Name + ": " + why);
                        continue;
                    }
                    GameInputBinding binding = new GameInputBinding { ActionId = control.Name, Device = device.JoyProName, Input = token, IsAxis = control.IsAxis };
                    if (control.IsAxis)
                    {
                        binding.Inverted = bind.Inverted != HotasInvert(control.Name);
                        binding.Tuning = new AxisTuning { Inverted = binding.Inverted, Slider = control.Type == "abs" };
                        binding.KeepTuning = control.Type == "abs";
                    }
                    result.Bindings.Add(binding);
                }
            }
            result.Steps.Add("Read " + state.Source + (state.Pilot != null ? " of pilot " + state.Pilot.Callsign : string.Empty) + "; devices: " + DescribeSlots(slots, map) + ".");
            // Whether JoyPro builds the same SDL GUID for these devices (what a free slot would get).
            List<string> guidChecks = slots.Where(s => map.ContainsKey(s.Index)).Select(s => "Joy-" + s.Index.ToString(CultureInfo.InvariantCulture) + " " +
                (string.Equals(SdlGuid(map[s.Index], devices), s.Guid, StringComparison.OrdinalIgnoreCase) ? "same" : "differs (" + SdlGuid(map[s.Index], devices) + ")")).ToList();
            if (guidChecks.Count > 0)
                result.Steps.Add("SDL GUID built by JoyPro: " + string.Join(", ", guidChecks) + ".");
            result.BindingsRead = result.Bindings.Count;
            return result;
        }

        private static string DescribeSlots(List<FsoSlot> slots, Dictionary<int, GameDevice> map)
        {
            if (slots.Count == 0)
                return "none assigned in the game's options";
            return string.Join(", ", slots.Select(s =>
            {
                GameDevice d;
                return "Joy-" + s.Index.ToString(CultureInfo.InvariantCulture) + " " + (map.TryGetValue(s.Index, out d) ? d.JoyProName : "(not connected, " + s.Vendor.ToString("X4", CultureInfo.InvariantCulture) + ":" + s.Product.ToString("X4", CultureInfo.InvariantCulture) + ")");
            }));
        }

        private static GameInputBinding ReadKey(FsoControl control, FsoBind bind)
        {
            int dik;
            bool alt;
            bool shift;
            if (control.IsAxis || !FsoNames.TryParseKey(bind.Input, out dik, out alt, out shift))
                return null;
            string key = GameKeyCodes.NameFromDik(dik);
            if (key.Length == 0)
                return null;
            List<string> reformers = new List<string>();
            if (alt)
                reformers.Add("LeftAlt§Keyboard§LeftAlt§False");
            if (shift)
                reformers.Add("LeftShift§Keyboard§LeftShift§False");
            return new GameInputBinding { ActionId = control.Name, Device = "Keyboard", Input = key, IsAxis = false, Reformers = reformers };
        }

        // The device's axes in SDL's order: DirectInput's present axes sorted by their DIJOYSTATE2 place.
        private static List<int> SdlAxes(GameDevice device, GameDeviceDirectory devices)
        {
            if (devices != null && !device.CapabilitiesKnown)
                devices.EnsureCapabilities(device, new List<string>());
            return (device.AxisSlots ?? new List<int>()).Where(a => a >= 0 && a <= 7).Distinct().OrderBy(a => a).ToList();
        }

        private static string DecodeJoy(FsoBind bind, GameDevice device, GameDeviceDirectory devices, out string why)
        {
            why = string.Empty;
            if (bind.IsAxis)
            {
                int index = Array.IndexOf(FsoNames.Axes, bind.Input);
                List<int> axes = SdlAxes(device, devices);
                if (index < 0)
                {
                    why = "unknown axis '" + bind.Input + "'.";
                    return string.Empty;
                }
                if (axes.Count == 0)
                    return GameInputTokens.AxisToken(index);
                if (index >= axes.Count)
                {
                    why = bind.Input + ": " + device.JoyProName + " has only " + axes.Count.ToString(CultureInfo.InvariantCulture) + " axes.";
                    return string.Empty;
                }
                return GameInputTokens.AxisToken(axes[index]);
            }
            int button;
            if (!int.TryParse(bind.Input, NumberStyles.Integer, CultureInfo.InvariantCulture, out button) || button < 0 || button >= FsoNames.JoyButtons + 4)
            {
                why = "unknown joystick input '" + bind.Input + "'.";
                return string.Empty;
            }
            if (button < FsoNames.JoyButtons)
                return GameInputTokens.ButtonToken(button);
            int direction = Array.IndexOf(FsoNames.HatButtonByDirection, button);
            return GameInputTokens.PovToken(0, direction);
        }

        // The direction FSO's file needs for an axis to work as JoyPro's (DCS's) direction. The absolute throttle needs
        // none: FSO 26 takes the raw axis 0..1 (controlsconfig.cpp joy_get_unscaled_reading, INVERTED = 1 - value) and
        // sets the speed to (1 - (value * 1.2 - 0.1)) * 100 % (playercontrol.cpp), so the low end of the raw axis is full
        // throttle - as in DCS, where a lever that reads low when pushed forward (most HOTAS throttles in DirectInput)
        // works without invert. Checked with a tester's own preset (VPC throttle, "AXIS") and JoyPro's export ("INVERTED,
        // AXIS" ran backwards in the game, 2.7.3). The relative throttle adds the centred reading, so the low end (pushed
        // forward) would slow down: it gets INVERTED. Stick axes work as they are.
        internal static bool HotasInvert(string control)
        {
            return control == "JOY_REL_THROTTLE_AXIS";
        }

        // JoyPro up to 2.7.3 read and wrote the absolute throttle with INVERTED flipped; the stored "how the file read
        // back" of such a bind is flipped likewise, so a file still as exported keeps the relation's own direction.
        public void UpgradeLegacyMetadata(string key, BindMetadata meta)
        {
            if (meta == null || string.IsNullOrEmpty(meta.FileTuning) || key == null ||
                !key.StartsWith("joy_abs_throttle_axis\u001E", StringComparison.OrdinalIgnoreCase))
                return;
            AxisTuning tuning = AxisTuning.Parse(meta.FileTuning);
            if (tuning == null)
                return;
            tuning.Inverted = !tuning.Inverted;
            meta.FileTuning = tuning.Format();
        }

        // ---- write ----

        private sealed class Want
        {
            public FsoControl Control;
            public FsoBind Bind;
            public GameInputBinding Binding;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            GameState state = LoadState(request.ConfigPath, result.Warnings, result.Errors);
            if (result.Errors.Count > 0 || state.Bindings == null)
                return result;
            FsoBind[][] baseBindings = Clone(state.Bindings);
            FsoBind[][] work = Clone(state.Bindings);
            List<FsoSlot> slots = LoadSlots(state.UserData, state.ModFolder, result.Warnings);
            Dictionary<int, GameDevice> map = MapSlots(slots, request.Devices);
            FsoIni settingsIni = FsoIni.Load(SettingsIniPath(state.UserData, state.ModFolder));
            List<string> settingSteps = new List<string>();
            HashSet<int> catalogIds = new HashSet<int>(state.Controls.Controls.Where(c => !c.Disabled && (request.Catalog == null || request.Catalog.Actions.Count == 0 || request.Catalog.Contains(c.Name))).Select(c => c.Id));

            // The binds as the file writes them.
            List<Want> wants = new List<Want>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                FsoControl control = state.Controls.Find(binding.ActionId);
                string reason = string.Empty;
                FsoBind bind = control == null ? FsoBind.None : Encode(binding, control, slots, map, request.Devices, settingsIni, settingSteps, out reason);
                if (control == null)
                    reason = "'" + binding.ActionId + "' is no FreeSpace Open control.";
                else if (control.Disabled)
                    reason = "'" + binding.ActionId + "' is switched off by " + mod.ShortName + " (its controls menu does not have it).";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                if (wants.Any(w => w.Control == control && w.Bind.SameInput(bind)))
                    continue;
                wants.Add(new Want { Control = control, Bind = bind, Binding = binding });
            }

            // Slots the export may clear: Joy-n with a connected device JoyPro does not keep.
            HashSet<int> owned = new HashSet<int>(map.Where(p => !p.Value.Kept).Select(p => p.Key));
            HashSet<int> used = new HashSet<int>(wants.Select(w => w.Bind.Joy).Where(j => j >= 0));
            bool add = request.Mode == GameExportMode.Add;
            int released = 0;
            foreach (FsoControl control in state.Controls.Controls.Where(c => catalogIds.Contains(c.Id)))
            {
                List<Want> mine = wants.Where(w => w.Control == control).ToList();
                for (int place = 0; place < 2; place++)
                {
                    FsoBind current = work[control.Id][place];
                    if (current.IsNone || mine.Any(w => w.Bind.SameInput(current)))
                        continue;
                    bool conflict = !add && wants.Any(w => w.Control != control && w.Bind.SameInput(current));
                    bool moved = request.Mode == GameExportMode.MergeOverwrite && mine.Count > 0;
                    bool release;
                    if (current.IsKeyboard)
                        release = request.IncludeKeyboard && (conflict || moved);
                    else if (current.IsMouse || current.Joy < 0 || !owned.Contains(current.Joy))
                        release = false;
                    else
                    {
                        bool usedSlot = used.Contains(current.Joy);
                        release = conflict || moved || (usedSlot && request.Mode == GameExportMode.Clean) || (!usedSlot && request.NukeUnusedDevices);
                    }
                    if (!release)
                        continue;
                    work[control.Id][place] = FsoBind.None;
                    released++;
                }
            }

            int written = 0;
            foreach (Want want in wants)
            {
                FsoBind[] places = work[want.Control.Id];
                int same = Array.FindIndex(places, p => p.SameInput(want.Bind));
                if (same >= 0)
                {
                    if (!places[same].Same(want.Bind) && !add)
                    {
                        places[same] = want.Bind;
                        written++;
                    }
                    continue;
                }
                if (add)
                {
                    FsoControl other = state.Controls.Controls.FirstOrDefault(c => c != want.Control && !c.Disabled && work[c.Id].Any(p => p.SameInput(want.Bind)));
                    if (other != null)
                    {
                        result.ConflictsKept++;
                        result.BindingsSkipped++;
                        result.SkippedTakenInputs.Add(new ExportSkippedBind
                        {
                            Game = GameId,
                            Device = want.Binding.Device,
                            Input = want.Binding.Input,
                            ModifiersText = Modifiers(want.Binding),
                            Relation = want.Binding.RelationName,
                            ActionId = want.Control.Name,
                            Action = state.Controls.TitleOf(want.Control),
                            GameActionId = other.Name,
                            GameAction = state.Controls.TitleOf(other)
                        });
                        continue;
                    }
                }
                Func<FsoBind, bool> heldByProfile = p => wants.Any(w => w != want && w.Control == want.Control && w.Bind.SameInput(p));
                int free = Array.FindIndex(places, p => p.IsNone);
                int sameDevice = want.Bind.IsKeyboard ? -1 : Array.FindIndex(places, p => !p.IsNone && p.Cid == want.Bind.Cid && !heldByProfile(p));
                int target = add ? free : request.Mode == GameExportMode.Merge ? (free >= 0 ? free : sameDevice) : (sameDevice >= 0 ? sameDevice : free);
                if (target < 0)
                {
                    string holders = "Primary: " + Words(places[0], map) + "; Secondary: " + Words(places[1], map);
                    result.ConflictsKept++;
                    result.BindingsSkipped++;
                    result.Warnings.Add("'" + state.Controls.TitleOf(want.Control) + "' (relation '" + want.Binding.RelationName + "'): " + want.Binding.Device + " " + want.Binding.Input +
                                        " was not written: FreeSpace Open keeps two inputs per control and both are in use (" + holders + ").");
                    result.SkippedTakenInputs.Add(new ExportSkippedBind
                    {
                        Game = GameId,
                        Device = want.Binding.Device,
                        Input = want.Binding.Input,
                        ModifiersText = Modifiers(want.Binding),
                        Relation = want.Binding.RelationName,
                        ActionId = want.Control.Name,
                        Action = state.Controls.TitleOf(want.Control),
                        GameAction = holders,
                        Reason = ExportSkippedBind.NoFreeSlot
                    });
                    continue;
                }
                places[target] = want.Bind;
                written++;
            }

            result.BindingsWritten = written;
            Dictionary<string, double> gameSettings = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (!gameSettings.ContainsKey(FsoGameSettings.DeadZone) && wants.Any(w => w.Binding.IsAxis && w.Binding.Deadzone > 0))
                result.Steps.Add("FreeSpace Open has one dead zone for all joystick axes; relation dead zones are not written. Set it with: engine ffb set --device * --game " + GameId + " --param " + FsoGameSettings.DeadZone + "=<0..45>.");
            WriteFiles(request, state, baseBindings, work, released, settingsIni, settingSteps, gameSettings, result);
            return result;
        }

        private static string Modifiers(GameInputBinding binding)
        {
            return string.Join("+", (binding.Reformers ?? new List<string>()).Select(r => r.Split('§')).Select(p => p.Length >= 3 ? p[1] + " " + p[2] : p[0]));
        }

        private static string Words(FsoBind bind, Dictionary<int, GameDevice> map)
        {
            if (bind.IsNone)
                return "free";
            if (bind.IsKeyboard)
                return "key " + bind.Input;
            if (bind.IsMouse)
                return "mouse " + bind.Input;
            GameDevice device;
            return (map.TryGetValue(bind.Joy, out device) ? device.JoyProName : "Joy-" + bind.Joy.ToString(CultureInfo.InvariantCulture)) + " " + (bind.IsAxis ? bind.Input : "button " + bind.Input);
        }

        private FsoBind Encode(GameInputBinding binding, FsoControl control, List<FsoSlot> slots, Dictionary<int, GameDevice> map, GameDeviceDirectory devices,
                               FsoIni settingsIni, List<string> settingSteps, out string reason)
        {
            reason = string.Empty;
            if (binding.IsKeyboard)
            {
                if (control.IsAxis)
                {
                    reason = "a FreeSpace Open axis cannot take a key.";
                    return FsoBind.None;
                }
                int dik;
                if (!GameKeyCodes.TryGetDik(binding.Input, out dik) || FsoNames.KeyName(dik).Length == 0)
                {
                    reason = "key '" + binding.Input + "' has no FreeSpace Open name.";
                    return FsoBind.None;
                }
                bool alt = false;
                bool shift = false;
                foreach (string reformer in binding.Reformers ?? new List<string>())
                {
                    string[] parts = reformer.Split('§');
                    string key = parts.Length >= 3 && string.Equals(parts[1], "Keyboard", StringComparison.OrdinalIgnoreCase) ? parts[2] : string.Empty;
                    if (key.EndsWith("Shift", StringComparison.OrdinalIgnoreCase))
                        shift = true;
                    else if (key.EndsWith("Alt", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Menu", StringComparison.OrdinalIgnoreCase))
                        alt = true;
                    else
                    {
                        reason = "FreeSpace Open keys only take Alt and Shift as modifiers.";
                        return FsoBind.None;
                    }
                }
                return FsoBind.Make("CID_KEYBOARD", "NONE", FsoNames.KeyInput(dik, alt, shift));
            }
            if (binding.Reformers != null && binding.Reformers.Count > 0)
            {
                reason = "FreeSpace Open has no joystick modifiers.";
                return FsoBind.None;
            }
            int slot = map.Where(p => string.Equals(p.Value.JoyProName, binding.Device, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).DefaultIfEmpty(-1).First();
            GameDevice device = devices == null ? null : devices.FindByJoyProName(binding.Device);
            if (slot < 0)
            {
                slot = AssignFreeSlot(device, devices, slots, map, settingsIni, settingSteps);
                if (slot < 0)
                {
                    reason = device == null
                        ? "the device is not connected."
                        : "the device is not one of the game's four devices; pick it as Device 1-4 in the game's options (or free a slot there) and export again.";
                    return FsoBind.None;
                }
            }
            if (device == null)
                device = map[slot];
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
            {
                reason = "unsupported input '" + binding.Input + "'.";
                return FsoBind.None;
            }
            string cid = FsoNames.JoyCid(slot);
            if (control.IsAxis != (input.Kind == JoyInputKind.Axis))
            {
                reason = control.IsAxis ? "'" + control.Name + "' is an axis; bind an axis." : "'" + control.Name + "' needs a button or the first hat.";
                return FsoBind.None;
            }
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    {
                        List<int> axes = SdlAxes(device, devices);
                        int index = axes.Count == 0 ? input.Index : axes.IndexOf(input.Index);
                        if (index < 0 || index >= FsoNames.Axes.Length)
                        {
                            reason = "the device has no " + binding.Input + " (or more than eight axes before it).";
                            return FsoBind.None;
                        }
                        bool inverted = binding.Inverted != HotasInvert(control.Name);
                        return FsoBind.Make(cid, inverted ? "INVERTED, AXIS" : "AXIS", FsoNames.Axes[index]);
                    }
                case JoyInputKind.Button:
                    if (input.Index >= FsoNames.JoyButtons)
                    {
                        reason = "FreeSpace Open reads buttons 1-128 only.";
                        return FsoBind.None;
                    }
                    return FsoBind.Make(cid, "NONE", input.Index.ToString(CultureInfo.InvariantCulture));
                case JoyInputKind.Pov:
                    if (input.Index != 0)
                    {
                        reason = "FreeSpace Open reads only the first hat of a device.";
                        return FsoBind.None;
                    }
                    int hat = input.Direction >= 0 && input.Direction < FsoNames.HatButtonByDirection.Length ? FsoNames.HatButtonByDirection[input.Direction] : -1;
                    if (hat < 0)
                    {
                        reason = "FreeSpace Open reads the hat in four directions only (no diagonals).";
                        return FsoBind.None;
                    }
                    return FsoBind.Make(cid, "NONE", hat.ToString(CultureInfo.InvariantCulture));
                default:
                    reason = "unsupported input '" + binding.Input + "'.";
                    return FsoBind.None;
            }
        }

        // A device the game's options do not use yet takes the first free Device 1-4 (the mod's settings get its SDL GUID
        // with device index -1, as Fate of the Galaxy writes them).
        private static int AssignFreeSlot(GameDevice device, GameDeviceDirectory devices, List<FsoSlot> slots, Dictionary<int, GameDevice> map, FsoIni settingsIni, List<string> steps)
        {
            if (device == null || !device.HasVidPid || device.Kept)
                return -1;
            for (int i = 0; i < FsoNames.Joysticks; i++)
            {
                if (slots.Any(s => s.Index == i))
                    continue;
                string guid = SdlGuid(device, devices);
                if (guid.Length != 32)
                    return -1;
                FsoSlot slot = new FsoSlot { Index = i, Guid = guid, Id = -1 };
                slots.Add(slot);
                map[i] = device;
                settingsIni.Set("Input", i == 0 ? "Joystick" : "Joystick" + i.ToString(CultureInfo.InvariantCulture), "{\"guid\":\"" + guid + "\",\"id\":-1}");
                steps.Add("Device " + (i + 1).ToString(CultureInfo.InvariantCulture) + " (Joy-" + i.ToString(CultureInfo.InvariantCulture) + ") = " + device.JoyProName + " (SDL GUID " + guid + ").");
                return i;
            }
            return -1;
        }

        private void WriteFiles(GameExportRequest request, GameState state, FsoBind[][] baseBindings, FsoBind[][] work, int released, FsoIni settingsIni,
                                List<string> settingSteps, Dictionary<string, double> gameSettings, GameIoResult result)
        {
            string presetPath = PresetPath(state.UserData, mod.Preset);
            bool ours = string.Equals(state.PresetName, mod.Preset, StringComparison.OrdinalIgnoreCase);
            bool changed = !FsoPresetFile.SameBindings(work, baseBindings, null);
            string existing = File.Exists(presetPath) ? File.ReadAllText(presetPath, Encoding.UTF8) : null;
            string rendered = FsoPresetFile.Render(work);
            bool presetChanged = existing == null ? (changed || !ours) : !string.Equals(existing, rendered, StringComparison.Ordinal) && (changed || !ours);

            // FSO ignores a preset file that binds everything as an earlier preset does (the default, a table preset or
            // another preset file before it by name) and the pilot then falls back to the defaults: the pilot gets that
            // preset's name instead.
            string duplicate = DuplicateOf(state, work, presetPath);
            string pilotPreset = duplicate ?? mod.Preset;

            List<string> steps = new List<string>();
            if (duplicate == null && presetChanged)
                steps.Add("Preset '" + mod.Preset + "' (" + presetPath + "): " + result.BindingsWritten.ToString(CultureInfo.InvariantCulture) + " input(s) written, " + released.ToString(CultureInfo.InvariantCulture) +
                          " released, the other controls as in " + state.Source + ".");
            else if (duplicate != null)
                steps.Add("The controls are the same as the preset '" + duplicate + "', which the pilot uses then (FSO ignores a second preset with the same binds).");

            string pilotText = null;
            if (state.Pilot != null)
            {
                string text = state.Pilot.Text;
                if (!string.Equals(state.Pilot.Preset, pilotPreset, StringComparison.Ordinal))
                {
                    text = state.Pilot.WithPreset(text, pilotPreset);
                    if (text == null)
                        result.Warnings.Add("The pilot " + state.Pilot.Callsign + " has no single preset entry; select the preset '" + pilotPreset + "' in the game's Controls menu.");
                    else
                        steps.Add("Pilot " + state.Pilot.Callsign + " now uses the preset '" + pilotPreset + "' (was '" + state.Pilot.Preset + "').");
                }
                if (text != null)
                {
                    text = ApplyPilotSettings(text, gameSettings, steps);
                    if (!string.Equals(text, state.Pilot.Text, StringComparison.Ordinal))
                        pilotText = text;
                }
            }
            else
            {
                steps.Add("No pilot yet: create one in the game, then pick the preset '" + mod.Preset + "' in Options > Controls (or export again).");
            }
            ApplyIniSettings(settingsIni, gameSettings, steps);

            List<KeyValuePair<string, byte[]>> saves = new List<KeyValuePair<string, byte[]>>();
            if (state.Pilot != null)
            {
                foreach (string csg in state.Pilot.CampaignSaves())
                {
                    byte[] updated = FsoCampaignSave.WithPreset(File.ReadAllBytes(csg), pilotPreset);
                    if (updated != null)
                    {
                        saves.Add(new KeyValuePair<string, byte[]>(csg, updated));
                        steps.Add("Campaign save " + Path.GetFileName(csg) + " uses the preset '" + pilotPreset + "' too.");
                    }
                }
            }
            steps.AddRange(settingSteps);

            bool writePreset = duplicate == null && presetChanged;
            if (!writePreset && pilotText == null && saves.Count == 0 && !settingsIni.Changed)
            {
                result.Steps.Add("The game's controls already match the profile (" + state.Source + ").");
                return;
            }
            if (request.DryRun)
            {
                result.Steps.AddRange(steps.Select(s => "Dry run: " + s));
                return;
            }
            if (writePreset)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(presetPath));
                GameExportSafety.BackupBeforeOverwrite(presetPath, result.Warnings);
                File.WriteAllText(presetPath, rendered, new UTF8Encoding(false));
                result.FilesWritten.Add(presetPath);
            }
            if (pilotText != null)
            {
                GameExportSafety.BackupBeforeOverwrite(state.Pilot.Path, result.Warnings);
                File.WriteAllText(state.Pilot.Path, pilotText, new UTF8Encoding(false));
                result.FilesWritten.Add(state.Pilot.Path);
            }
            foreach (KeyValuePair<string, byte[]> save in saves)
            {
                GameExportSafety.BackupBeforeOverwrite(save.Key, result.Warnings);
                File.WriteAllBytes(save.Key, save.Value);
                result.FilesWritten.Add(save.Key);
            }
            if (settingsIni.Changed)
            {
                GameExportSafety.BackupBeforeOverwrite(settingsIni.Path, result.Warnings);
                Directory.CreateDirectory(Path.GetDirectoryName(settingsIni.Path));
                File.WriteAllText(settingsIni.Path, settingsIni.Render(), new UTF8Encoding(false));
                result.FilesWritten.Add(settingsIni.Path);
            }
            result.Steps.AddRange(steps);
        }

        // The name of an earlier preset FSO would keep instead of ours (same bindings), or null.
        private string DuplicateOf(GameState state, FsoBind[][] work, string presetPath)
        {
            foreach (KeyValuePair<string, FsoBind[][]> preset in state.Controls.Presets)
            {
                if (FsoPresetFile.SameBindings(work, preset.Value, null))
                    return preset.Key;
            }
            string folder = Path.GetDirectoryName(presetPath) ?? string.Empty;
            if (!Directory.Exists(folder))
                return null;
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(name, mod.Preset, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Compare(name, mod.Preset, StringComparison.OrdinalIgnoreCase) > 0)
                    break;
                FsoBind[][] other = FsoPresetFile.Read(file, new List<string>());
                if (other != null && FsoPresetFile.SameBindings(work, other, null))
                    return name;
            }
            return null;
        }

        // ---- game-wide settings (engine ffb set --device * --game <id>) ----

        private static string ApplyPilotSettings(string text, Dictionary<string, double> values, List<string> steps)
        {
            foreach (KeyValuePair<string, string> key in FsoGameSettings.PilotKeys)
            {
                double value;
                if (!values.TryGetValue(key.Key, out value))
                    continue;
                int v = FsoGameSettings.Clamp(key.Key, value);
                string updated = FsoPilot.WithSetting(text, key.Value, v);
                if (updated != null && !string.Equals(updated, text, StringComparison.Ordinal))
                {
                    text = updated;
                    steps.Add("Pilot setting " + key.Value + " = " + v.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            return text;
        }

        private static void ApplyIniSettings(FsoIni ini, Dictionary<string, double> values, List<string> steps)
        {
            foreach (KeyValuePair<string, string> key in FsoGameSettings.IniKeys)
            {
                double value;
                if (!values.TryGetValue(key.Key, out value))
                    continue;
                string v = FsoGameSettings.Clamp(key.Key, value).ToString(CultureInfo.InvariantCulture);
                if (ini.Get("Input", key.Value) == v)
                    continue;
                ini.Set("Input", key.Value, v);
                steps.Add(Path.GetFileName(ini.Path) + " [Input] " + key.Value + " = " + v + ".");
            }
        }

        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string userData = FsoInstall.UserDataOf(configPath);
            FsoPilot pilot = !IsPreset(configPath) && File.Exists(configPath) ? FsoPilot.Read(configPath, warnings) : null;
            FsoIni ini = FsoIni.Load(SettingsIniPath(userData, ModFolderFor(userData)));
            int number;
            int? deadZone = pilot != null ? pilot.DeadZone : null;
            int? sensitivity = pilot != null ? pilot.Sensitivity : null;
            if (!deadZone.HasValue && int.TryParse(ini.Get("Input", "JoystickDeadZone"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                deadZone = number;
            if (!sensitivity.HasValue && int.TryParse(ini.Get("Input", "JoystickSensitivity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                sensitivity = number;
            if (deadZone.HasValue)
                values[FsoGameSettings.DeadZone] = deadZone.Value;
            if (sensitivity.HasValue)
                values[FsoGameSettings.Sensitivity] = sensitivity.Value;
            return values;
        }

        public string SettingsGame { get { return GameId; } }

        // The mod's settings file holds the device slots and the axis response next to the pilot.
        public List<string> SettingsFiles(string configPath)
        {
            string userData = FsoInstall.UserDataOf(configPath);
            return userData.Length == 0 ? new List<string>() : new List<string> { SettingsIniPath(userData, ModFolderFor(userData)) };
        }

        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null).Count > 0;
        }

        public GameIoResult WriteSettings(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            string userData = FsoInstall.UserDataOf(request.ConfigPath);
            Dictionary<string, double> values = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            List<string> steps = new List<string>();
            FsoIni ini = FsoIni.Load(SettingsIniPath(userData, ModFolderFor(userData)));
            ApplyIniSettings(ini, values, steps);
            FsoPilot pilot = !IsPreset(request.ConfigPath) && File.Exists(request.ConfigPath) ? FsoPilot.Read(request.ConfigPath, result.Warnings) : null;
            string pilotText = pilot == null ? null : ApplyPilotSettings(pilot.Text, values, steps);
            if (steps.Count == 0)
            {
                result.Steps.Add("The " + mod.ShortName + " settings already match.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.AddRange(steps.Select(s => "Dry run: " + s));
                return result;
            }
            if (ini.Changed)
            {
                GameExportSafety.BackupBeforeOverwrite(ini.Path, result.Warnings);
                Directory.CreateDirectory(Path.GetDirectoryName(ini.Path));
                File.WriteAllText(ini.Path, ini.Render(), new UTF8Encoding(false));
                result.FilesWritten.Add(ini.Path);
            }
            if (pilot != null && !string.Equals(pilotText, pilot.Text, StringComparison.Ordinal))
            {
                GameExportSafety.BackupBeforeOverwrite(pilot.Path, result.Warnings);
                File.WriteAllText(pilot.Path, pilotText, new UTF8Encoding(false));
                result.FilesWritten.Add(pilot.Path);
            }
            result.Steps.AddRange(steps);
            return result;
        }

        // ---- catalog ----

        // The mod's controls menu: every control it does not switch off, with the English text the menu shows.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string modFolder = string.IsNullOrWhiteSpace(sourcePath) ? null : FsoInstall.ResolveModFolder(sourcePath, mod.ModId);
            if (modFolder == null && string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                modFolder = install != null && install.Installed ? FsoInstall.ResolveModFolder(install.InstallPath, mod.ModId) : null;
            }
            if (modFolder == null)
            {
                result.Errors.Add(mod.ShortName + " was not found. Pass --path with its Knossos folder (the one with Knossos.NET.exe or kn_portable) or the mod folder (" + mod.ModId + "-<version> with mod.json).");
                return null;
            }
            FsoModControls controls = FsoModControls.Load(modFolder, result.Warnings);
            GameCatalog catalog = BuildCatalog(controls);
            catalog.SourcePath = modFolder;
            result.Steps.Add("Read the controls of " + modFolder + " (version " + FsoInstall.VersionOf(modFolder) + "): " + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) + " controls in the menu, " +
                             controls.Controls.Count(c => c.Disabled).ToString(CultureInfo.InvariantCulture) + " switched off by the mod" +
                             (controls.TableSource.Length > 0 ? " (" + controls.TableSource + ")" : ", no controlconfigdefaults.tbl: FreeSpace Open's own list") + ".");
            return catalog;
        }

        public GameCatalog BuiltInCatalog()
        {
            return BuildCatalog(new FsoModControls());
        }

        private GameCatalog BuildCatalog(FsoModControls controls)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (FsoControl control in controls.Controls.Where(c => !c.Disabled).OrderBy(c => TabOrder(c.Tab)).ThenBy(c => c.Id))
            {
                catalog.Add(new GameAction
                {
                    Id = control.Name,
                    Title = Readable(controls.TitleOf(control)),
                    IsAxis = control.IsAxis,
                    Category = FsoNames.TabTitle(control.Tab),
                    NativeId = control.Id.ToString(CultureInfo.InvariantCulture)
                });
            }
            return catalog;
        }

        private static int TabOrder(char tab)
        {
            return "TSWC".IndexOf(tab) < 0 ? 9 : "TSWC".IndexOf(tab);
        }

        // Menu texts as they read best in a list: no tabs, single spaces.
        private static string Readable(string title)
        {
            return string.Join(" ", (title ?? string.Empty).Replace('\t', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
