using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // IL-2 Sturmovik: Cliffs of Dover (Steam 63950, Launcher.exe) and Cliffs of Dover Blitz (Team Fusion, Steam 754530,
    // Launcher64.exe). Both keep their binds in Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini
    // (the pre-Blitz Team Fusion mod: "... cliffs of dover - MOD"): "[HotKey <section>]" lines "<trigger> [<trigger>...]=
    // <command>", all triggers held together, the last one fires. Joysticks: "<device>+Key<n>" (0-based), axes
    // "<device>+AXE_<axis>=<1|-1><command>" (sign = inversion), hats "Pov<angle> <device>+Pov". Device names: Blitz writes
    // "<DirectInput name, spaces as _>-<hex id>", the original game "Joystick", "Joystick1", ... in DirectInput order.
    // Axis settings per command in [Config pilotMove] (Il2CodAxisModel). Checked against both installs and Blitz's binaries;
    // Blitz's controls page not yet (docs/games/il2-cliffs-of-dover.md).
    internal sealed class Il2CodIntegration : Il2LegacyIntegration, IGameProcessFilter
    {
        public const string CodId = "IL2CoD";
        public const string BlitzId = "IL2CoDBlitz";
        public const string UserFolderName = "il-2 sturmovik cliffs of dover";
        public const string ConfigFileName = "confUser.ini";
        private static readonly string[] SectionNames = { "pilotMove", "pilot", "common", "turret", "view", "viewSet", "viewPan", "viewSnap", "timeSpeed", "orders", "BOB", "vehicle" };
        private static readonly string[] AxisSectionNames = { "pilotMove" };
        private static readonly string[] FlightControls = { "aileron", "elevator", "rudder" };

        private readonly bool blitz;

        public Il2CodIntegration(bool blitz)
        {
            this.blitz = blitz;
        }

        public bool Blitz { get { return blitz; } }
        public override string GameId { get { return blitz ? BlitzId : CodId; } }
        public override string DisplayName { get { return blitz ? "IL-2 Sturmovik: Cliffs of Dover Blitz" : "IL-2 Sturmovik: Cliffs of Dover"; } }
        public override IReadOnlyList<string> Aliases { get { return blitz ? new[] { "il2codblitz", "codblitz", "blitz" } : new[] { "il2cod", "cod", "cliffsofdover" }; } }
        public override string DbFolder { get { return GameId; } }
        public override string Notes
        {
            get
            {
                return "Close Cliffs of Dover before exporting (it saves confUser.ini on exit). Turn off Steam Cloud for the game if it brings old binds back. " +
                       (blitz ? "Blitz and the original game share confUser.ini. " : "The original game names joysticks by Windows' order (Joystick, Joystick1, ...). ") +
                       "Checked against the installed games' files; binds made on Blitz's controls page not yet.";
            }
        }
        public override IReadOnlyList<string> ProcessNames { get { return blitz ? new[] { "Launcher64" } : new[] { "Launcher" }; } }

        // Steam's folder names of the two games.
        public const string CodFolderName = "IL-2 Sturmovik Cliffs of Dover";
        public const string BlitzFolderName = "IL-2 Sturmovik Cliffs of Dover Blitz";

        // "Launcher.exe" is also Falcon BMS's launcher: only the exe in this game's install folder (the one JoyPro found
        // or the user set, or Steam's folder name) is the game.
        public bool IsGameProcess(string exePath)
        {
            string folder = Path.GetDirectoryName(exePath ?? string.Empty) ?? string.Empty;
            if (folder.Length == 0)
                return true;
            GameInstall install = GameInstalls.Find(GameId, new List<string>());
            if (install != null && !string.IsNullOrWhiteSpace(install.InstallPath) &&
                string.Equals(Path.GetFullPath(install.InstallPath).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(Path.GetFileName(folder.TrimEnd('\\')), blitz ? BlitzFolderName : CodFolderName, StringComparison.OrdinalIgnoreCase);
        }
        // Blitz 5.026+: 16 devices (8 before); the original game 8.
        public override int MaxJoysticks { get { return blitz ? 16 : 8; } }
        protected override IReadOnlyList<string> Sections { get { return SectionNames; } }
        protected override IReadOnlyList<string> AxisSections { get { return AxisSectionNames; } }
        protected override string CtrlWord { get { return "Control"; } }
        // A user's file (Steam Cloud copy of the original, 2015): "Joystick2+Key4=1underc" in [HotKey pilotMove].
        protected override bool AxisCommandsTakeButtons { get { return true; } }

        protected override int MaxInputsPerCommand(bool axis)
        {
            return axis ? 1 : 0;
        }

        // The ground vehicles (Desert Wings - Tobruk) are not flight: JoyPro never releases their binds.
        protected override bool IsFlightSection(string section)
        {
            return !string.Equals(section, "vehicle", StringComparison.OrdinalIgnoreCase);
        }

        protected override string CategoryOf(string section)
        {
            switch (section)
            {
                case "pilotMove": return "Axes";
                case "pilot": return "Aircraft";
                case "common": return "Common";
                case "turret": return "Gunner";
                case "view":
                case "viewSet":
                case "viewPan":
                case "viewSnap": return "Views";
                case "timeSpeed": return "Time";
                case "orders": return "Orders";
                case "BOB": return "Game";
                case "vehicle": return "Vehicle";
                default: return section;
            }
        }

        // ---- paths ---------------------------------------------------------------------------------------------

        public static string UserFolder(bool modFolder)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "1C SoftClub", UserFolderName + (modFolder ? " - MOD" : string.Empty));
        }

        public override List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            foreach (string folder in blitz ? new[] { UserFolder(false) } : new[] { UserFolder(false), UserFolder(true) })
            {
                string path = Path.Combine(folder, ConfigFileName);
                if (File.Exists(path))
                    paths.Add(path);
            }
            return paths;
        }

        protected override IEnumerable<string> CatalogSources(string sourcePath, List<string> warnings)
        {
            List<string> sources = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                if (File.Exists(sourcePath))
                    sources.Add(sourcePath);
                else if (Directory.Exists(sourcePath))
                    sources.Add(Path.Combine(sourcePath, ConfigFileName));
                else
                    warnings.Add("Not found: " + sourcePath);
                return sources;
            }
            sources.AddRange(DiscoverConfigPaths(warnings));
            // The game's default binds in the install folder (a never-started install has only these).
            GameInstall install = GameInstalls.Find(GameId, warnings);
            if (install != null && !string.IsNullOrWhiteSpace(install.InstallPath))
                sources.Add(Path.Combine(install.InstallPath, ConfigFileName));
            return sources;
        }

        // ---- devices -------------------------------------------------------------------------------------------

        // Blitz: DirectInput's product name (blanks kept) with every ' ' and '+' as '_' (seen in the installed game, checked 2026-10-12).
        public static string SanitizedName(string name)
        {
            return (name ?? string.Empty).Replace(' ', '_').Replace('+', '_');
        }

        public static string BlitzNameOf(GameDevice device)
        {
            return SanitizedName(string.IsNullOrEmpty(device.ProductName) ? device.InstanceName : device.ProductName);
        }

        // Blitz's id after the name: a 31-bit table hash of the device's interface path (DIPROP_GUIDANDPATH, as ANSI
        // bytes) printed as "%X" (no zero padding). That is why it changes with the USB port. Seen in Blitz
        // (2026-10-12): h = ((h << 8) | byte) ^ T[h >> 24], T = the MSB-first CRC table of 0x23788D5E masked to 31 bits.
        private static readonly uint[] BlitzHashTable = BuildBlitzHashTable();

        private static uint[] BuildBlitzHashTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int bit = 0; bit < 8; bit++)
                    c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x23788D5Eu : c << 1;
                table[i] = c & 0x7FFFFFFFu;
            }
            return table;
        }

        public static string BlitzPathHash(string path)
        {
            uint h = 0;
            foreach (char ch in path ?? string.Empty)
                h = ((h << 8) | (byte)(ch < 0x80 ? ch : '?')) ^ BlitzHashTable[h >> 24];
            return h.ToString("X", CultureInfo.InvariantCulture);
        }

        // The id of a connected device; empty when its interface path cannot be read.
        public static string BlitzIdOf(GameDevice device, GameDeviceDirectory devices)
        {
            if (string.IsNullOrEmpty(device.HidPath) && devices != null)
                devices.EnsureCapabilities(device, null);
            return string.IsNullOrEmpty(device.HidPath) ? string.Empty : BlitzPathHash(device.HidPath);
        }

        private static bool TrySplitDevice(string token, out string device, out string rest)
        {
            device = string.Empty;
            rest = string.Empty;
            int plus = token.LastIndexOf('+');
            if (plus <= 0 || plus == token.Length - 1)
                return false;
            device = token.Substring(0, plus);
            rest = token.Substring(plus + 1);
            return true;
        }

        // A device name of the file -> the connected device (null when none matches).
        private GameDevice ResolveDevice(string fileDevice, GameDeviceDirectory devices)
        {
            if (devices == null)
                return null;
            if (fileDevice == "Joystick")
                return devices.Devices.FirstOrDefault(d => d.EnumerationIndex == 0);
            int number;
            if (fileDevice.StartsWith("Joystick", StringComparison.Ordinal) &&
                int.TryParse(fileDevice.Substring("Joystick".Length), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                return devices.Devices.FirstOrDefault(d => d.EnumerationIndex == number);
            int dash = fileDevice.LastIndexOf('-');
            if (dash <= 0)
                return null;
            string name = fileDevice.Substring(0, dash);
            string id = fileDevice.Substring(dash + 1);
            List<GameDevice> named = devices.Devices.Where(d => string.Equals(BlitzNameOf(d), name, StringComparison.Ordinal)).ToList();
            GameDevice exact = named.FirstOrDefault(d => string.Equals(BlitzIdOf(d, devices), id, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
            // The same device on another USB port (new id): taken when it is the only one with that name.
            return named.Count == 1 ? named[0] : null;
        }

        // The file's name of a connected device: Blitz reuses a name the file already has for it, else builds one.
        private string FileDeviceName(GameDevice device, LegacyIniFile file, GameDeviceDirectory devices)
        {
            if (!blitz)
                return device.EnumerationIndex == 0 ? "Joystick" : "Joystick" + device.EnumerationIndex.ToString(CultureInfo.InvariantCulture);
            string sanitized = BlitzNameOf(device);
            // The game's own name for the device on this port first (an older line may still name another port).
            string own = BlitzIdOf(device, devices);
            if (own.Length > 0)
                return sanitized + "-" + own;
            if (file != null)
            {
                foreach (string token in AllDeviceTokens(file))
                {
                    int dash = token.LastIndexOf('-');
                    if (dash > 0 && string.Equals(token.Substring(0, dash), sanitized, StringComparison.Ordinal) && ReferenceEquals(ResolveDevice(token, devices), device))
                        return token;
                }
            }
            // Without the interface path the id is unknown: no name the game would match.
            return string.Empty;
        }

        private IEnumerable<string> AllDeviceTokens(LegacyIniFile file)
        {
            foreach (string section in Sections)
            {
                foreach (LegacyIniFile.Entry entry in file.Entries(SectionHeader(section)))
                {
                    foreach (string token in entry.Key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string device;
                        string rest;
                        if (TrySplitDevice(token, out device, out rest))
                            yield return device;
                    }
                }
            }
        }

        protected override string CanonicalKey(string key, GameDeviceDirectory devices)
        {
            if (!blitz || devices == null)
                return key;
            List<string> tokens = new List<string>();
            foreach (string token in (key ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string device;
                string rest;
                GameDevice resolved = TrySplitDevice(token, out device, out rest) ? ResolveDevice(device, devices) : null;
                string name = resolved == null ? string.Empty : FileDeviceName(resolved, null, devices);
                tokens.Add(name.Length > 0 ? name + "+" + rest : token);
            }
            return string.Join(" ", tokens);
        }

        protected override string DeviceTokenOf(string key)
        {
            foreach (string token in key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string device;
                string rest;
                if (TrySplitDevice(token, out device, out rest))
                    return device;
            }
            return string.Empty;
        }

        // ---- inputs --------------------------------------------------------------------------------------------

        // One trigger: a joystick button/axis/hat or a key.
        private sealed class Trigger
        {
            public string Device = string.Empty;      // JoyPro device or "Keyboard"
            public string FileDevice = string.Empty;
            public string Input = string.Empty;
            public bool IsAxis;
            public bool IsPov;
        }

        private bool TryDecodeTrigger(string token, GameDeviceDirectory devices, out Trigger trigger, out string problem)
        {
            trigger = null;
            problem = string.Empty;
            string fileDevice;
            string rest;
            if (TrySplitDevice(token, out fileDevice, out rest))
            {
                GameDevice device = ResolveDevice(fileDevice, devices);
                if (device == null)
                {
                    problem = DisplayName + " device '" + fileDevice + "' is not connected (or its id changed); its binds were not read.";
                    return false;
                }
                trigger = new Trigger { Device = device.JoyProName, FileDevice = fileDevice };
                int number;
                if (rest.StartsWith("Key", StringComparison.Ordinal) && int.TryParse(rest.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    trigger.Input = GameInputTokens.ButtonToken(number);
                else if (rest.StartsWith("AXE_", StringComparison.Ordinal) && Il2LegacyInput.AxisIndex(rest.Substring(4)) >= 0)
                {
                    trigger.Input = GameInputTokens.AxisToken(Il2LegacyInput.AxisIndex(rest.Substring(4)));
                    trigger.IsAxis = true;
                }
                else if (rest == "Pov")
                    trigger.IsPov = true;
                else
                {
                    problem = DisplayName + ": unknown joystick input '" + token + "'; kept as it is.";
                    trigger = null;
                    return false;
                }
                return true;
            }
            string key = Il2LegacyInput.ModifierToJoyPro(token) ?? Il2LegacyInput.KeyToJoyPro(token);
            if (key == null)
                return false;
            trigger = new Trigger { Device = "Keyboard", Input = key };
            return true;
        }

        private static string Reformer(Trigger trigger)
        {
            if (trigger.Device == "Keyboard")
                return Il2LegacyInput.KeyboardReformer(trigger.Input);
            return trigger.FileDevice + " " + trigger.Input + "§" + trigger.Device + "§" + trigger.Input + "§False";
        }

        protected override bool TryDecodeKey(string key, GameDeviceDirectory devices, out Il2LegacyInput2 input, out string problem)
        {
            input = null;
            problem = string.Empty;
            List<string> tokens = key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count == 0)
                return false;

            // A hat: "Pov<angle>" plus "<device>+Pov"; the other triggers are modifiers.
            int direction = -1;
            string angleToken = tokens.FirstOrDefault(t => Il2LegacyInput.TryParsePovAngle(t, out direction));
            List<Trigger> triggers = new List<Trigger>();
            foreach (string token in tokens.Where(t => t != angleToken))
            {
                Trigger trigger;
                if (!TryDecodeTrigger(token, devices, out trigger, out problem))
                    return false;
                triggers.Add(trigger);
            }
            Trigger main;
            if (angleToken != null)
            {
                main = triggers.LastOrDefault(t => t.IsPov);
                if (main == null)
                {
                    // "Pov0=PanUp" without a device: a default the game could not resolve.
                    return false;
                }
                main.Input = GameInputTokens.PovToken(0, direction);
            }
            else
            {
                main = triggers.LastOrDefault();
                if (main == null || main.IsPov)
                    return false;
            }
            input = new Il2LegacyInput2 { Device = main.Device, Input = main.Input, IsAxis = main.IsAxis, FileDevice = main.FileDevice };
            foreach (Trigger modifier in triggers.Where(t => t != main))
            {
                if (modifier.IsAxis || modifier.IsPov)
                {
                    input = null;
                    return false;
                }
                input.Reformers.Add(Reformer(modifier));
            }
            return true;
        }

        private bool TryEncodeTrigger(string device, string inputToken, GameDeviceDirectory devices, LegacyIniFile file, bool modifier, out string text, out string fileDevice, out string problem)
        {
            text = string.Empty;
            fileDevice = string.Empty;
            problem = string.Empty;
            if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                text = (modifier ? Il2LegacyInput.ModifierFromJoyPro(inputToken, CtrlWord) : null) ?? Il2LegacyInput.KeyFromJoyPro(inputToken);
                if (text == null)
                    problem = "the key '" + inputToken + "' has no " + DisplayName + " name.";
                return text != null;
            }
            GameDevice joystick = devices == null ? null : devices.FindByJoyProName(device);
            if (joystick == null || joystick.EnumerationIndex < 0)
            {
                problem = "device '" + device + "' is not connected, so its " + DisplayName + " name is unknown; connect it and export again.";
                return false;
            }
            if (!blitz && joystick.EnumerationIndex >= MaxJoysticks)
            {
                problem = "device '" + device + "' is Windows game controller " + (joystick.EnumerationIndex + 1).ToString(CultureInfo.InvariantCulture) + "; " + DisplayName + " reads the first " + MaxJoysticks.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }
            fileDevice = FileDeviceName(joystick, file, devices);
            if (fileDevice.Length == 0)
            {
                problem = "the device path of '" + device + "' could not be read, so its " + DisplayName + " id is unknown.";
                return false;
            }
            JoyInput input;
            if (!GameInputTokens.TryParse(inputToken, out input))
            {
                problem = "unsupported input '" + inputToken + "'.";
                return false;
            }
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    text = fileDevice + "+AXE_" + Il2LegacyInput.AxisNames[input.Index];
                    return !modifier;
                case JoyInputKind.Button:
                    if (input.Index >= 128)
                    {
                        problem = DisplayName + " reads buttons 1..128.";
                        return false;
                    }
                    text = fileDevice + "+Key" + input.Index.ToString(CultureInfo.InvariantCulture);
                    return true;
                case JoyInputKind.Pov:
                    if (input.Index > 0 || modifier)
                    {
                        problem = DisplayName + " files name one hat per device (the first).";
                        return false;
                    }
                    text = Il2LegacyInput.PovAngle(input.Direction) + " " + fileDevice + "+Pov";
                    return true;
                default:
                    problem = "unsupported input '" + inputToken + "'.";
                    return false;
            }
        }

        protected override bool TryEncodeKey(GameInputBinding binding, GameDeviceDirectory devices, LegacyIniFile file, out string key, out string fileDevice, out string problem)
        {
            key = string.Empty;
            List<string> parts = new List<string>();
            foreach (string reformer in binding.Reformers ?? new List<string>())
            {
                string device;
                string modifierKey;
                string text;
                string ignored;
                if (!Il2LegacyInput.TrySplitReformer(reformer, out device, out modifierKey))
                {
                    fileDevice = string.Empty;
                    problem = "unreadable modifier '" + reformer + "'.";
                    return false;
                }
                if (!TryEncodeTrigger(device, modifierKey, devices, file, true, out text, out ignored, out problem))
                {
                    fileDevice = string.Empty;
                    problem = problem.Length == 0 ? "the modifier cannot be written." : problem;
                    return false;
                }
                parts.Add(text);
            }
            string main;
            if (!TryEncodeTrigger(binding.Device, binding.Input, devices, file, false, out main, out fileDevice, out problem))
                return false;
            if (binding.IsKeyboard)
                fileDevice = string.Empty;
            parts.Add(main);
            key = string.Join(" ", parts);
            return true;
        }

        protected override string DecodeValue(string section, string value, out bool inverted)
        {
            string text = (value ?? string.Empty).Trim();
            inverted = false;
            if (string.Equals(section, "pilotMove", StringComparison.OrdinalIgnoreCase))
            {
                if (text.StartsWith("-1", StringComparison.Ordinal))
                {
                    inverted = true;
                    return text.Substring(2);
                }
                if (text.StartsWith("1", StringComparison.Ordinal))
                    return text.Substring(1);
            }
            return text;
        }

        protected override string EncodeValue(string section, string command, bool axis, bool inverted)
        {
            if (string.Equals(section, "pilotMove", StringComparison.OrdinalIgnoreCase))
                return (inverted ? "-1" : "1") + command;
            return command;
        }

        // ---- axis settings ([Config pilotMove]) ----------------------------------------------------------------

        private const string ConfigSection = "Config pilotMove";

        public static double[] ParseNumbers(string text)
        {
            List<double> values = new List<double>();
            foreach (string part in (text ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return null;
                values.Add(v);
            }
            return values.ToArray();
        }

        // How many values a command's line has: 3 on the flight controls, 5 on the throttles, else 2.
        public static int ValueCount(string command)
        {
            if (FlightControls.Contains(command, StringComparer.OrdinalIgnoreCase))
                return 3;
            string c = command.ToLowerInvariant();
            if (c == "power" || (c.StartsWith("throttle", StringComparison.Ordinal) && c != "throttle_wep"))
                return 5;
            return 2;
        }

        protected override void ReadTuning(Il2LegacyContext context, string section, string command, Il2LegacyInput2 input, GameInputBinding binding)
        {
            double[] values = ParseNumbers(context.File.Get(ConfigSection, command));
            if (values == null || values.Length == 0)
                return;
            AxisTuning tuning = new AxisTuning { Inverted = binding.Inverted };
            if (values.Length == 3)
            {
                tuning.Native[Il2CodAxisModel.Sensitivity] = values[0];
                tuning.Native[Il2CodAxisModel.DeadZoneCentre] = values[1];
                tuning.Native[Il2CodAxisModel.DeadZoneEnd] = values[2];
            }
            else if (values.Length == 2)
            {
                tuning.Slider = true;
                tuning.Native[Il2CodAxisModel.DeadZoneLow] = values[0];
                tuning.Native[Il2CodAxisModel.DeadZoneHigh] = values[1];
            }
            else
            {
                tuning.Slider = true;
                for (int i = 0; i < Math.Min(Il2CodAxisModel.RawCount, values.Length); i++)
                    tuning.Native[Il2CodAxisModel.RawKey(i)] = values[i];
            }
            binding.Tuning = tuning;
            binding.KeepTuning = true;
        }

        private static double[] WantedValues(AxisTuning tuning, int count)
        {
            string[] keys = count == 3
                ? new[] { Il2CodAxisModel.Sensitivity, Il2CodAxisModel.DeadZoneCentre, Il2CodAxisModel.DeadZoneEnd }
                : count == 2
                    ? new[] { Il2CodAxisModel.DeadZoneLow, Il2CodAxisModel.DeadZoneHigh }
                    : Enumerable.Range(0, Il2CodAxisModel.RawCount).Select(Il2CodAxisModel.RawKey).ToArray();
            if (keys.Any(k => !tuning.Native.ContainsKey(k)))
                return null;
            return keys.Select(k => Math.Max(0.0, Math.Min(1.0, tuning.Native[k]))).ToArray();
        }

        protected override void WriteTunings(Il2LegacyContext context, List<Il2LegacyWanted> axes, GameExportRequest request, GameIoResult result)
        {
            foreach (Il2LegacyWanted want in axes)
            {
                if (want.Binding.Tuning == null)
                    continue;
                string existingText = context.File.Get(ConfigSection, want.Command);
                double[] existing = ParseNumbers(existingText);
                int count = existing != null && existing.Length > 0 ? existing.Length : ValueCount(want.Command);
                double[] values = WantedValues(want.Binding.Tuning, count);
                if (values == null)
                    continue;
                // Equal to the file within the profile's precision: the game's own digits stay.
                if (existing != null && existing.Length == values.Length && existing.Zip(values, (a, b) => Math.Abs(a - b) < 1e-6).All(x => x))
                    continue;
                if (existing != null && existing.Length > 0 && request.Mode == GameExportMode.Merge && values.All(v => v == 0))
                    continue;
                string text = string.Join(" ", values.Select(v => AxisTuning.Num(v)));
                context.File.Set(ConfigSection, want.Command, text);
                result.Steps.Add("Relation '" + want.Binding.RelationName + "' (" + Il2LegacyInput.Id(want.Section, want.Command) + "): [" + ConfigSection + "] " + want.Command + "=" + text + ".");
            }
        }

        // ---- catalog -------------------------------------------------------------------------------------------

        protected override IEnumerable<GameAction> OwnActions()
        {
            List<string> rows = new List<string>
            {
                "pilotMove|aileron|a|Ailerons (roll)", "pilotMove|elevator|a|Elevator (pitch)", "pilotMove|rudder|a|Rudder (yaw)", "pilotMove|throttle|a|Throttle",
                "pilotMove|throttle_wep|a|Throttle WEP / boost", "pilotMove|pitch|a|Propeller pitch", "pilotMove|mix|a|Mixture", "pilotMove|radiator|a|Radiator",
                "pilotMove|oilradiator|a|Oil radiator", "pilotMove|compressor|a|Supercharger", "pilotMove|carbheater|a|Carburettor heater",
                "pilotMove|trimaileron|a|Aileron trim", "pilotMove|trimelevator|a|Elevator trim", "pilotMove|trimrudder|a|Rudder trim", "pilotMove|flaps|a|Flaps",
                "pilotMove|leslats|a|Slats", "pilotMove|underc|a|Undercarriage", "pilotMove|airbrake|a|Air brake", "pilotMove|bombbaydoor|a|Bomb bay doors",
                "pilotMove|brakes|a|Wheel brakes", "pilotMove|brakeslt|a|Left wheel brake", "pilotMove|brakesrt|a|Right wheel brake",
                "pilotMove|throttle0|a|Throttle engine 1", "pilotMove|throttle1|a|Throttle engine 2", "pilotMove|pitch0|a|Propeller pitch engine 1", "pilotMove|pitch1|a|Propeller pitch engine 2",
                "pilotMove|mix0|a|Mixture engine 1", "pilotMove|mix1|a|Mixture engine 2", "pilotMove|radiator0|a|Radiator engine 1", "pilotMove|radiator1|a|Radiator engine 2",

                "pilot|trigger0|b|Fire guns", "pilot|trigger1|b|Fire cannons", "pilot|trigger2|b|Weapon 3", "pilot|trigger3|b|Weapon 4", "pilot|trigger01|b|Fire guns and cannons",
                "pilot|dropbombs|b|Drop bombs", "pilot|bombbaydoors|b|Bomb bay doors", "pilot|abwurf_arm|b|Arm bombs",
                "pilot|uc|b|Undercarriage up/down", "pilot|uc_down_emer|b|Undercarriage emergency down", "pilot|landingflaps|b|Flaps toggle", "pilot|landingflaps_d|b|Flaps down",
                "pilot|landingflaps_u|b|Flaps up", "pilot|airbrake|b|Air brake", "pilot|wheelbrakex|b|Wheel brakes", "pilot|wheelbrakeltx|b|Left wheel brake", "pilot|wheelbrakertx|b|Right wheel brake",
                "pilot|brakeshoes01|b|Chocks", "pilot|tailskid_lock|b|Tail wheel lock",
                "pilot|thtl_plus|b|Throttle up", "pilot|thtl_minus|b|Throttle down", "pilot|thtl_wep|b|Boost (WEP)", "pilot|thtl_110|b|Throttle 110 %",
                "pilot|pitch_plus|b|Propeller pitch up", "pilot|pitch_minus|b|Propeller pitch down", "pilot|pitch_auto|b|Propeller pitch auto", "pilot|pitch_m_togl|b|Propeller pitch mode",
                "pilot|mix_plus|b|Mixture richer", "pilot|mix_minus|b|Mixture leaner", "pilot|rad_plus|b|Radiator open", "pilot|rad_minus|b|Radiator close", "pilot|rad_auto|b|Radiator auto",
                "pilot|oilrad_plus|b|Oil radiator open", "pilot|oilrad_minus|b|Oil radiator close",
                "pilot|aileron_leftx|b|Aileron left", "pilot|aileron_rightx|b|Aileron right", "pilot|elevator_upx|b|Elevator up", "pilot|elevator_downx|b|Elevator down",
                "pilot|rudder_leftx|b|Rudder left", "pilot|rudder_rightx|b|Rudder right",
                "pilot|aileront_left|b|Aileron trim left", "pilot|aileront_right|b|Aileron trim right", "pilot|aileront_back|b|Aileron trim neutral",
                "pilot|elevatort_nup|b|Elevator trim nose up", "pilot|elevatort_ndown|b|Elevator trim nose down", "pilot|elevatort_back|b|Elevator trim neutral",
                "pilot|ruddert_left|b|Rudder trim left", "pilot|ruddert_right|b|Rudder trim right", "pilot|ruddert_back|b|Rudder trim neutral",
                "pilot|enginei01|b|Engine start/stop", "pilot|engselect_all|b|Select all engines", "pilot|mags_next|b|Magnetos next", "pilot|mags_prev|b|Magnetos previous",
                "pilot|extinguisher|b|Fire extinguisher", "pilot|pitotheater|b|Pitot heat",

                "common|autopilot|b|Autopilot", "common|bailout|b|Bail out", "common|door_toggle|b|Canopy open/close", "common|door_jettison|b|Jettison canopy", "common|map|b|Map",
                "common|cste_lights|b|Navigation lights", "common|csec_lights|b|Cockpit lights", "common|cpri_lights|b|Instrument lights",
                "turret|trt_park_toggle|b|Turret park",
                "view|insideView|b|Cockpit view", "view|toggleAim|b|Gun sight view", "view|ViewFov+|b|Zoom out", "view|ViewFov-|b|Zoom in",
                "viewPan|PanUp|b|Pan up", "viewPan|PanDown|b|Pan down", "viewPan|PanLeft|b|Pan left", "viewPan|PanRight|b|Pan right", "viewPan|PanReset|b|Pan reset",
                "viewSnap|SnapPanSwitch|b|Snap / pan switch",
                "BOB|pause|b|Pause", "BOB|ScreenShot|b|Screenshot"
            };
            return ParseOwnRows(rows);
        }
    }
}
