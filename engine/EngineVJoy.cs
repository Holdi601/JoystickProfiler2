using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using JoyPro.Engine.Games;
using JoyPro.Engine.VJoy;
using Microsoft.Win32;

namespace JoyPro.Engine
{
    // engine vjoy ...: virtual joystick mode (vJoy + HidHide), docs/virtual-joystick.md.
    //
    // Safety: everything that changes the PC (HidHide's configuration, vJoyConfig) runs only on an explicit command
    // (apply-hidhide, restore-hidhide, recover, configure-vjoy), elevated with exactly one UAC prompt, and never with
    // --dry-run. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files in that folder (tests): nothing real is
    // read or changed then.
    internal static partial class EngineVJoy
    {
        public const string FakeVariable = "JOYPRO_VJOY_FAKE";
        public const string HidHideDefaultPath = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
        private const string VJoyDefaultFolder = @"C:\Program Files\vJoy";
        private const int VJoyVendorId = 0x1234;
        private const int VJoyProductId = 0xBEAD;

        private static readonly Dictionary<string, string[]> BuiltInProcesses = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", new[] { "DCS" } },
            { "IL2Game", new[] { "Il-2" } },
            { "StarCitizen", new[] { "StarCitizen" } }
        };

        private static readonly Dictionary<string, string> BuiltInNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", "DCS World" },
            { "IL2Game", "IL-2 Sturmovik: Great Battles" },
            { "StarCitizen", "Star Citizen" }
        };

        // ------------------------------------------------------------------ settings in the profile

        public static VJoySettings LoadSettings()
        {
            VJoySettings settings = VJoySettings.FromJson(InternalDataManagement.VirtualJoystickConfig);
            // A mode set for a game that is one game per version now (MSFS, XPlane) holds for every version.
            if (settings != null && settings.Games != null)
            {
                foreach (string legacy in settings.Games.Keys.Where(LegacyGameIds.IsLegacy).ToList())
                {
                    string mode = settings.Games[legacy];
                    settings.Games.Remove(legacy);
                    foreach (string version in LegacyGameIds.Expand(legacy))
                    {
                        if (!settings.Games.ContainsKey(version))
                            settings.Games[version] = mode;
                    }
                }
            }
            return settings;
        }

        public static void SaveSettings(VJoySettings settings)
        {
            InternalDataManagement.VirtualJoystickConfig = settings == null || settings.IsEmpty ? string.Empty : settings.ToJson();
        }

        public static string DataFolder()
        {
            return Path.Combine(GameInstalls.DataRoot(), "VirtualJoystick");
        }

        public static string JournalPath()
        {
            return Path.Combine(DataFolder(), "hidhide-session.txt");
        }

        private static string FakeFolder()
        {
            string value = Environment.GetEnvironmentVariable(FakeVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        // ------------------------------------------------------------------ games

        public static List<string> GameIds()
        {
            List<string> ids = new List<string> { "DCS", "IL2Game", "StarCitizen" };
            ids.AddRange(GameRegistry.All.Select(g => g.GameId).Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)));
            return ids;
        }

        public static string ResolveGame(string value)
        {
            string id = GameInstalls.Resolve(value);
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            return GameIds().FirstOrDefault(g => string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        public static string GameName(string gameId)
        {
            string name;
            if (BuiltInNames.TryGetValue(gameId, out name))
                return name;
            IGameIntegration game;
            return GameRegistry.TryResolve(gameId, out game) ? game.DisplayName : gameId;
        }

        // A one-device game (IGameDeviceLimit), or null.
        public static IGameDeviceLimit DeviceLimit(string gameId)
        {
            IGameIntegration game;
            return GameRegistry.TryResolve(gameId, out game) ? game as IGameDeviceLimit : null;
        }

        // The virtual mode JoyPro suggests for a game ("virtual" / "xbox"), empty when the game reads all devices.
        public static string SuggestedMode(string gameId)
        {
            IGameDeviceLimit limit = DeviceLimit(gameId);
            return limit == null ? string.Empty : (limit.SuggestedMode ?? string.Empty);
        }

        public static List<string> ProcessNames(string gameId)
        {
            string[] names;
            if (BuiltInProcesses.TryGetValue(gameId, out names))
                return names.ToList();
            IGameIntegration game;
            return GameRegistry.TryResolve(gameId, out game) ? game.ProcessNames.ToList() : new List<string>();
        }

        private static bool NodeMatches(string gameId, string nodeGame)
        {
            string normalized = string.IsNullOrWhiteSpace(nodeGame) ? "DCS" : nodeGame.Trim();
            if (string.Equals(normalized, gameId, StringComparison.OrdinalIgnoreCase))
                return true;
            IGameIntegration game;
            return GameRegistry.TryResolve(gameId, out game) && GameRegistry.GameMatches(game, normalized);
        }

        private static bool NodeActive(RelationItem node)
        {
            return node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(v => v);
        }

        // A JoyPro device string of a game controller ("Name {GUID}"), not the keyboard or mouse.
        public static bool IsJoystickDevice(string device)
        {
            string name;
            Guid guid;
            return GameDeviceDirectory.TryParseJoyProName(device, out name, out guid);
        }

        public static bool IsVJoyName(string device)
        {
            return (device ?? string.Empty).IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Every physical input the games use (relations with an active node of the game and a bind on a game
        // controller, and the modifier buttons of those binds).
        public static List<VJoyUsedInput> CollectUsedInputs(IEnumerable<string> games)
        {
            List<string> gameList = games.ToList();
            Dictionary<string, VJoyUsedInput> used = new Dictionary<string, VJoyUsedInput>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                if (bind == null)
                    continue;
                List<string> relationGames = gameList.Where(g => relation.NODES.Any(n => n != null && NodeMatches(g, n.Game) && NodeActive(n))).ToList();
                if (relationGames.Count == 0)
                    continue;
                string input = relation.ISAXIS ? bind.JAxis : bind.JButton;
                AddUsed(used, bind.Joystick, input, relation.NAME ?? entry.Key, relationGames);
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier != null)
                        AddUsed(used, modifier.device, modifier.key, (relation.NAME ?? entry.Key) + " (modifier " + modifier.name + ")", relationGames);
                }
            }
            return used.Values.OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddUsed(Dictionary<string, VJoyUsedInput> used, string device, string token, string relation, List<string> games)
        {
            if (!IsJoystickDevice(device) || IsVJoyName(device))
                return;
            VJoyInput input;
            int direction;
            if (!VJoyInput.TryFromBindToken(token, out input, out direction))
                return;
            string source = device.Trim();
            string slotToken = input.ToSlotToken();
            string key = VJoyPlanner.Key(source, slotToken);
            VJoyUsedInput item;
            if (!used.TryGetValue(key, out item))
            {
                item = new VJoyUsedInput { Source = source, Input = slotToken };
                used[key] = item;
            }
            if (!item.Relations.Contains(relation, StringComparer.OrdinalIgnoreCase))
                item.Relations.Add(relation);
            foreach (string game in games)
            {
                if (!item.Games.Contains(game, StringComparer.OrdinalIgnoreCase))
                    item.Games.Add(game);
            }
        }

        public static VJoyPlan BuildPlan(VJoySettings settings, IDictionary<int, VJoyDeviceCaps> caps, bool reset, bool prune)
        {
            List<VJoyUsedInput> used = CollectUsedInputs(settings.VirtualGames());
            VJoyPlan plan = VJoyPlanner.Plan(settings, used, caps, reset, prune, AvoidAxes(settings));
            foreach (KeyValuePair<string, List<string>> game in LimitWarnings(settings, plan, used))
                plan.Warnings.AddRange(game.Value);
            return plan;
        }

        // Per virtual (vJoy) game: the slots of its inputs and its functions' outputs that lie above what the game
        // reads of a joystick (GameInputLimits).
        public static Dictionary<string, List<string>> LimitWarnings(VJoySettings settings, VJoyPlan plan, List<VJoyUsedInput> used)
        {
            Dictionary<string, List<string>> output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, VJoyUsedInput> byKey = used.GroupBy(u => VJoyPlanner.Key(u.Source, u.Input), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (string game in settings.VirtualGames())
            {
                List<string> warnings = new List<string>();
                List<KeyValuePair<int, string>> slotTargets = plan.Slots
                    .Where(s => { VJoyUsedInput u; return byKey.TryGetValue(VJoyPlanner.Key(s.Source, s.Input), out u) && u.Games.Contains(game, StringComparer.OrdinalIgnoreCase); })
                    .Select(s => new KeyValuePair<int, string>(s.Device, s.Target)).ToList();
                warnings.AddRange(GameInputLimits.Check(game, GameName(game), slotTargets, "the inputs on"));
                List<KeyValuePair<int, string>> functionTargets = plan.FunctionTargets.Where(f => string.Equals(f.Source, game, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new KeyValuePair<int, string>(f.Device, f.Target)).ToList();
                warnings.AddRange(GameInputLimits.Check(game, GameName(game), functionTargets, "the virtual function outputs on"));
                if (warnings.Count > 0)
                    output[game] = warnings;
            }
            return output;
        }

        // The vJoy axes the virtual-mode (vJoy) games must not see (IGameDeviceLimit.AvoidVJoyAxes): axis index -> why.
        public static Dictionary<int, string> AvoidAxes(VJoySettings settings)
        {
            Dictionary<int, string> avoid = new Dictionary<int, string>();
            foreach (string game in settings.VirtualGames())
            {
                IGameDeviceLimit limit = DeviceLimit(game);
                if (limit == null || limit.AvoidVJoyAxes == null)
                    continue;
                foreach (string name in limit.AvoidVJoyAxes)
                {
                    int axis = VJoyConfigArgs.AxisIndex(name);
                    if (axis >= 0 && !avoid.ContainsKey(axis))
                        avoid[axis] = GameName(game) + " takes a joystick with " + name.ToUpperInvariant() + " for an Xbox pad";
                }
            }
            return avoid;
        }

        // ---- the virtual Xbox pad (mode "xbox")

        // The game's gamepad layout as JoyPro knows it: action id -> Xbox control. It comes from the game's catalog
        // (actions.tsv, built from the game files): a native id that is an Unreal gamepad key ("Gamepad_LeftX",
        // "Gamepad_FaceButton_Bottom") pins the action to that control.
        public static Dictionary<string, string> GamepadLayout(string gameId, List<string> warnings)
        {
            Dictionary<string, string> layout = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IGameIntegration game;
            if (!GameRegistry.TryResolve(gameId, out game))
                return layout;
            GameCatalog catalog = GameCatalogStore.Load(game, warnings);
            IGameGamepadLayout fixedLayout = game as IGameGamepadLayout;
            if (fixedLayout != null)
            {
                foreach (KeyValuePair<string, string> entry in fixedLayout.GamepadLayout(catalog, warnings))
                {
                    string token = XboxControls.FromUnrealKey(entry.Value);
                    if (token.Length > 0)
                        layout[entry.Key] = token;
                }
                return layout;
            }
            foreach (GameAction action in catalog.Actions)
            {
                string token = XboxControls.FromUnrealKey(action.NativeId);
                if (token.Length > 0)
                    layout[action.Id] = token;
            }
            return layout;
        }

        // The physical inputs an Xbox-target game uses (its relations' binds and their modifier buttons), with the
        // control its layout pins each to.
        public static List<XboxUse> CollectXboxUses(string gameId, Dictionary<string, string> layout)
        {
            Dictionary<string, XboxUse> used = new Dictionary<string, XboxUse>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                if (bind == null)
                    continue;
                List<string> actions = relation.NODES.Where(n => n != null && NodeMatches(gameId, n.Game) && NodeActive(n)).Select(n => (n.ID ?? string.Empty).Trim()).ToList();
                if (actions.Count == 0)
                    continue;
                string name = relation.NAME ?? entry.Key;
                AddXboxUse(used, bind.Joystick, relation.ISAXIS ? bind.JAxis : bind.JButton, name, actions, layout);
                XboxUse own;
                VJoyInput ownInput;
                int ownDirection;
                if (relation.ISAXIS && bind.Inverted == true && IsJoystickDevice(bind.Joystick) && VJoyInput.TryFromBindToken(bind.JAxis, out ownInput, out ownDirection) &&
                    used.TryGetValue(VJoyPlanner.Key(bind.Joystick.Trim(), ownInput.ToSlotToken()), out own))
                    own.Invert = true;
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier != null)
                        AddXboxUse(used, modifier.device, modifier.key, name + " (modifier " + modifier.name + ")", new List<string>(), layout);
                }
            }
            return used.Values.OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddXboxUse(Dictionary<string, XboxUse> used, string device, string token, string relation, List<string> actions, Dictionary<string, string> layout)
        {
            if (!IsJoystickDevice(device) || IsVJoyName(device))
                return;
            VJoyInput input;
            int direction;
            if (!VJoyInput.TryFromBindToken(token, out input, out direction))
                return;
            string slotToken = input.ToSlotToken();
            string key = VJoyPlanner.Key(device.Trim(), slotToken);
            XboxUse item;
            if (!used.TryGetValue(key, out item))
            {
                item = new XboxUse { Source = device.Trim(), Input = slotToken };
                used[key] = item;
            }
            if (!item.Relations.Contains(relation, StringComparer.OrdinalIgnoreCase))
                item.Relations.Add(relation);
            foreach (string action in actions)
            {
                if (!item.Actions.Contains(action, StringComparer.OrdinalIgnoreCase))
                    item.Actions.Add(action);
                string pinned;
                if (!layout.TryGetValue(action, out pinned))
                    continue;
                if (item.Pinned.Length == 0)
                {
                    item.Pinned = pinned;
                    item.PinnedAction = action;
                }
                else if (input.Kind == VJoyInputKind.Axis && XboxControls.IsTrigger(item.Pinned) && XboxControls.IsTrigger(pinned) &&
                         !string.Equals(item.Pinned, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    // The same axis on the two trigger actions (Ascend RT / Descend LT): split at the centre.
                    item.Pinned = XboxControls.Triggers2;
                    item.PinnedAction = item.PinnedAction + " + " + action;
                }
            }
        }

        public static List<XboxPlan> BuildXboxPlans(VJoySettings settings, bool reset, bool prune, List<string> warnings)
        {
            List<XboxPlan> plans = new List<XboxPlan>();
            foreach (string game in settings.XboxGames())
            {
                Dictionary<string, string> layout = GamepadLayout(game, warnings);
                plans.Add(XboxPlanner.Plan(game, settings.XboxTable(game), CollectXboxUses(game, layout), reset, prune));
            }
            return plans;
        }

        public static void ApplyXboxPlans(VJoySettings settings, List<XboxPlan> plans)
        {
            foreach (string game in settings.XboxSlots.Keys.Where(g => !settings.IsXbox(g)).ToList())
                settings.XboxSlots.Remove(game);
            foreach (XboxPlan plan in plans)
            {
                if (plan.Slots.Count == 0)
                    settings.XboxSlots.Remove(plan.Game);
                else
                    settings.XboxSlots[plan.Game] = plan.Slots;
            }
        }

        // ViGEmBus (Nefarius), the driver of the virtual Xbox pad. JoyPro never installs it; it points to the official
        // installer.
        public const string ViGEmInstallerUrl = "https://github.com/nefarius/ViGEmBus/releases";

        internal sealed class ViGEmInfo
        {
            public bool Installed { get; set; }
            public bool Running { get; set; }
            public string Version { get; set; }
            public bool Fake { get; set; }

            public ViGEmInfo()
            {
                Version = string.Empty;
            }
        }

        // The device name the export uses for binds on the virtual Xbox pad (inputs XB_*); games that store gamepad
        // binds (IGameGamepadBinds) write them, the others skip them.
        public const string XboxDeviceName = "Xbox 360 Controller (JoyPro virtual pad)";

        public static bool WritesGamepadBinds(string gameId)
        {
            IGameIntegration game;
            return GameRegistry.TryResolve(gameId, out game) && game is IGameGamepadBinds;
        }

        public static ViGEmInfo ProbeViGEm()
        {
            string fake = FakeFolder();
            if (fake != null)
            {
                string version;
                ReadFakeValues(Path.Combine(fake, "vjoy.txt")).TryGetValue("vigem", out version);
                bool installed = !string.IsNullOrWhiteSpace(version) && !string.Equals(version, "no", StringComparison.OrdinalIgnoreCase);
                return new ViGEmInfo { Fake = true, Installed = installed, Running = installed, Version = installed ? version : string.Empty };
            }
            ViGEmInfo info = new ViGEmInfo();
            try
            {
                using (RegistryKey service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus"))
                    info.Installed = service != null;
                foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null)
                            continue;
                        foreach (string name in key.GetSubKeyNames())
                        {
                            using (RegistryKey app = key.OpenSubKey(name))
                            {
                                string display = app == null ? null : app.GetValue("DisplayName") as string;
                                if (display != null && display.IndexOf("ViGEm Bus", StringComparison.OrdinalIgnoreCase) >= 0)
                                    info.Version = (app.GetValue("DisplayVersion") as string) ?? string.Empty;
                            }
                        }
                    }
                }
                if (info.Installed)
                {
                    using (System.ServiceProcess.ServiceController controller = new System.ServiceProcess.ServiceController("ViGEmBus"))
                        info.Running = controller.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is InvalidOperationException || ex is Win32Exception)
            {
            }
            return info;
        }

        // ------------------------------------------------------------------ the PC: vJoy

        internal sealed class VJoyInstallInfo
        {
            public bool Installed { get; set; }
            public string Version { get; set; }
            public string Folder { get; set; }
            public string InterfaceDll { get; set; }
            public string ConfigTool { get; set; }
            public bool Fake { get; set; }

            public VJoyInstallInfo()
            {
                Version = Folder = InterfaceDll = ConfigTool = string.Empty;
            }
        }

        public static VJoyInstallInfo ProbeVJoy()
        {
            string fake = FakeFolder();
            if (fake != null)
            {
                Dictionary<string, string> values = ReadFakeValues(Path.Combine(fake, "vjoy.txt"));
                string version;
                values.TryGetValue("installed", out version);
                return new VJoyInstallInfo
                {
                    Fake = true,
                    Installed = !string.IsNullOrWhiteSpace(version) && !string.Equals(version, "no", StringComparison.OrdinalIgnoreCase),
                    Version = version ?? string.Empty,
                    Folder = fake,
                    InterfaceDll = Path.Combine(fake, "vJoyInterface.dll"),
                    ConfigTool = Path.Combine(fake, "vJoyConfig.exe")
                };
            }

            VJoyInstallInfo info = new VJoyInstallInfo();
            string folder = VJoyDefaultFolder;
            foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null)
                            continue;
                        foreach (string name in key.GetSubKeyNames())
                        {
                            using (RegistryKey app = key.OpenSubKey(name))
                            {
                                string display = app == null ? null : app.GetValue("DisplayName") as string;
                                if (display == null || display.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                info.Version = (app.GetValue("DisplayVersion") as string) ?? string.Empty;
                                string location = app.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                                    folder = location.TrimEnd('\\');
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                {
                }
            }
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            info.Folder = folder;
            info.InterfaceDll = Path.Combine(folder, arch, "vJoyInterface.dll");
            info.ConfigTool = Path.Combine(folder, arch, "vJoyConfig.exe");
            bool service;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\services\vjoy"))
                    service = key != null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                service = false;
            }
            info.Installed = service && File.Exists(info.InterfaceDll);
            return info;
        }

        // The configured vJoy devices (1..16) from the driver's registry descriptors (read-only).
        public static Dictionary<int, VJoyDeviceCaps> ReadVJoyDevices()
        {
            Dictionary<int, VJoyDeviceCaps> devices = new Dictionary<int, VJoyDeviceCaps>();
            for (int id = 1; id <= VJoySettings.MaxDevices; id++)
                devices[id] = new VJoyDeviceCaps { Id = id };
            string fake = FakeFolder();
            if (fake != null)
            {
                foreach (string line in ReadFakeLines(Path.Combine(fake, "vjoy.txt")))
                {
                    VJoyDeviceCaps caps = ParseFakeDevice(line);
                    if (caps != null)
                        devices[caps.Id] = caps;
                }
                return devices;
            }
            try
            {
                using (RegistryKey parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\services\vjoy\Parameters"))
                {
                    if (parameters == null)
                        return devices;
                    for (int id = 1; id <= VJoySettings.MaxDevices; id++)
                    {
                        using (RegistryKey device = parameters.OpenSubKey("Device" + id.ToString("00", CultureInfo.InvariantCulture)))
                        {
                            byte[] descriptor = device == null ? null : device.GetValue("HidReportDesctiptor") as byte[];
                            if (descriptor != null)
                                devices[id] = VJoyDescriptor.Parse(id, descriptor);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
            }
            return devices;
        }

        // "device=1 axes=x,y,z buttons=32 povs=4 discrete=0 ffb=no"
        private static VJoyDeviceCaps ParseFakeDevice(string line)
        {
            if (!line.StartsWith("device=", StringComparison.OrdinalIgnoreCase))
                return null;
            Dictionary<string, string> parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
            int id;
            if (!int.TryParse(parts["device"], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 1 || id > VJoySettings.MaxDevices)
                return null;
            VJoyDeviceCaps caps = new VJoyDeviceCaps { Id = id, Configured = true };
            string value;
            if (parts.TryGetValue("axes", out value))
                caps.Axes = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(VJoyConfigArgs.AxisIndex).Where(a => a >= 0).Distinct().OrderBy(a => a).ToList();
            caps.Buttons = FakeInt(parts, "buttons");
            caps.ContinuousPovs = FakeInt(parts, "povs");
            caps.DiscretePovs = FakeInt(parts, "discrete");
            caps.ForceFeedback = parts.TryGetValue("ffb", out value) && string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
            return caps;
        }

        private static int FakeInt(Dictionary<string, string> parts, string key)
        {
            string value;
            int number;
            return parts.TryGetValue(key, out value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static string FakeDeviceLine(VJoyDeviceCaps caps)
        {
            return "device=" + caps.Id.ToString(CultureInfo.InvariantCulture) +
                   " axes=" + string.Join(",", caps.Axes.Select(VJoyConfigArgs.AxisName)) +
                   " buttons=" + caps.Buttons.ToString(CultureInfo.InvariantCulture) +
                   " povs=" + caps.ContinuousPovs.ToString(CultureInfo.InvariantCulture) +
                   " discrete=" + caps.DiscretePovs.ToString(CultureInfo.InvariantCulture) +
                   " ffb=" + (caps.ForceFeedback ? "yes" : "no");
        }

        private static List<string> ReadFakeLines(string path)
        {
            if (!File.Exists(path))
                return new List<string>();
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToList();
        }

        private static Dictionary<string, string> ReadFakeValues(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in ReadFakeLines(path))
            {
                int split = line.IndexOf('=');
                if (split > 0 && !line.StartsWith("device=", StringComparison.OrdinalIgnoreCase))
                    values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }
            return values;
        }

        // A vJoy device as DirectInput (and therefore the games) sees it.
        internal sealed class VJoyPresence
        {
            public int Id { get; set; }
            public string DeviceName { get; set; }
            public string HidPath { get; set; }
            public string InstancePath { get; set; }
        }

        // The physical and vJoy game controllers that are connected now, with their HID paths.
        internal sealed class DeviceScan
        {
            public GameDeviceDirectory Directory { get; set; }
            public List<VJoyPresence> VJoy { get; set; }
            public Dictionary<string, string> GamingInstancePaths { get; set; }
            public List<string> Warnings { get; set; }

            public DeviceScan()
            {
                VJoy = new List<VJoyPresence>();
                Warnings = new List<string>();
                GamingInstancePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public string NameOf(int vjoyId)
            {
                VJoyPresence presence = VJoy.FirstOrDefault(v => v.Id == vjoyId);
                return presence == null ? string.Empty : presence.DeviceName;
            }

            public GameDevice Find(string joyProName)
            {
                return Directory == null ? null : Directory.FindByJoyProName(joyProName);
            }

            // HidHide's device instance path of a HID interface path.
            public string InstancePathOf(string hidPath)
            {
                if (string.IsNullOrWhiteSpace(hidPath))
                    return string.Empty;
                string exact;
                if (GamingInstancePaths.TryGetValue(hidPath.Trim(), out exact))
                    return exact;
                return InstancePathFromInterface(hidPath);
            }
        }

        public static DeviceScan ScanDevices(IHidHideCli hidHide, Dictionary<int, VJoyDeviceCaps> vjoyDevices)
        {
            DeviceScan scan = new DeviceScan();
            scan.Directory = GameDeviceDirectory.Discover(scan.Warnings);
            scan.GamingInstancePaths = ReadGamingInstancePaths(hidHide, scan.Warnings);
            List<int> configured = vjoyDevices.Values.Where(v => v.Configured).Select(v => v.Id).OrderBy(v => v).ToList();
            List<GameDevice> vjoy = scan.Directory.Devices
                .Where(d => (d.VendorId == VJoyVendorId && d.ProductId == VJoyProductId) || IsVJoyName(d.InstanceName))
                .ToList();
            foreach (GameDevice device in vjoy)
            {
                scan.Directory.EnsureCapabilities(device, scan.Warnings);
                int id = 0;
                Match collection = Regex.Match(device.HidPath ?? string.Empty, @"&col(?<n>[0-9a-f]{2})", RegexOptions.IgnoreCase);
                if (collection.Success)
                {
                    // vJoy puts one top-level collection per configured device, in device order.
                    int index = int.Parse(collection.Groups["n"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (index >= 1 && index <= configured.Count)
                        id = configured[index - 1];
                }
                else if (configured.Count == 1 && vjoy.Count == 1)
                {
                    id = configured[0];
                }
                if (id == 0)
                {
                    scan.Warnings.Add("Cannot tell which vJoy device " + device.JoyProName + " is (" + (device.HidPath ?? "no HID path") + ").");
                    continue;
                }
                scan.VJoy.Add(new VJoyPresence { Id = id, DeviceName = device.JoyProName, HidPath = device.HidPath ?? string.Empty, InstancePath = scan.InstancePathOf(device.HidPath) });
            }
            return scan;
        }

        // "\\?\hid#vid_231d&pid_0125#c&8abe679&0&0000#{4d1e55b2-...}" -> "HID\VID_231D&PID_0125\c&8abe679&0&0000" (the
        // fallback when HidHide's own device list does not have the device).
        public static string InstancePathFromInterface(string hidPath)
        {
            string value = (hidPath ?? string.Empty).Trim();
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
                value = value.Substring(4);
            string[] parts = value.Split('#');
            if (parts.Length < 3)
                return string.Empty;
            string deviceId = Regex.Replace(parts[1].ToUpperInvariant(), "&COL", "&Col");
            return parts[0].ToUpperInvariant() + "\\" + deviceId + "\\" + parts[2];
        }

        // HidHide's list of gaming devices (HidHideCLI --dev-gaming): symbolic link -> device instance path. Only asked
        // in the elevated step (without administrator rights the query can hang) and read from the fake's file.
        private static Dictionary<string, string> ReadGamingInstancePaths(IHidHideCli hidHide, List<string> warnings)
        {
            Dictionary<string, string> output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string json = null;
            string fake = FakeFolder();
            if (fake != null)
            {
                string path = Path.Combine(fake, "hidhide-gaming.json");
                json = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            else
            {
                HidHideCliProcess real = hidHide as HidHideCliProcess;
                if (real != null && real.Installed && real.Elevated)
                {
                    TimedResult answer = real.Query("--dev-gaming");
                    if (answer.TimedOut)
                        warnings.Add("HidHideCLI --dev-gaming did not answer in time and was ended; device paths are derived from the HID paths.");
                    else
                        json = answer.Output;
                }
            }
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return output;
            try
            {
                object root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
                foreach (object groupObject in root as object[] ?? new object[0])
                {
                    Dictionary<string, object> group = groupObject as Dictionary<string, object>;
                    object devicesObject;
                    if (group == null || !group.TryGetValue("devices", out devicesObject))
                        continue;
                    foreach (object deviceObject in devicesObject as object[] ?? new object[0])
                    {
                        Dictionary<string, object> device = deviceObject as Dictionary<string, object>;
                        object link;
                        object instance;
                        if (device != null && device.TryGetValue("symbolicLink", out link) && device.TryGetValue("deviceInstancePath", out instance) &&
                            link is string && instance is string)
                            output[(string)link] = (string)instance;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                warnings.Add("HidHide's device list could not be read: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add("HidHide's device list could not be read: " + ex.Message);
            }
            return output;
        }

        // ------------------------------------------------------------------ the PC: HidHide

        // elevatedContext: the engine's elevated step (reads everything, may apply). Otherwise reads only, and the
        // hidden-device list stays unknown when HidHide denies it.
        public static IHidHideCli CreateHidHide(bool elevatedContext = false)
        {
            string fake = FakeFolder();
            if (fake != null)
                return new LoggingFakeHidHide(fake, elevatedContext);
            return new HidHideCliProcess(FindHidHideCli(), elevatedContext && IsElevated());
        }

        private static string FindHidHideCli()
        {
            string overridePath = Environment.GetEnvironmentVariable("JOYPRO_HIDHIDE_CLI");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath.Trim();
            if (File.Exists(HidHideDefaultPath))
                return HidHideDefaultPath;
            try
            {
                foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null)
                            continue;
                        foreach (string name in key.GetSubKeyNames())
                        {
                            using (RegistryKey app = key.OpenSubKey(name))
                            {
                                string display = app == null ? null : app.GetValue("DisplayName") as string;
                                string location = app == null ? null : app.GetValue("InstallLocation") as string;
                                if (display == null || display.IndexOf("HidHide", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrWhiteSpace(location))
                                    continue;
                                string candidate = Path.Combine(location, "x64", "HidHideCLI.exe");
                                if (File.Exists(candidate))
                                    return candidate;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
            }
            return HidHideDefaultPath;
        }

        public static string HidHideVersion()
        {
            if (FakeFolder() != null)
                return "fake";
            try
            {
                string path = FindHidHideCli();
                return File.Exists(path) ? (FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        // Time limits for HidHideCLI (JOYPRO_HIDHIDE_TIMEOUT_MS for the reads; apply gets three times as long, at
        // least 30 s). A call that runs over is ended with its child processes: no HidHideCLI is ever left running
        // (a hanging one makes every other HidHideCLI call fail with "Access is denied").
        private static int ReadTimeoutMs()
        {
            int value;
            string raw = Environment.GetEnvironmentVariable("JOYPRO_HIDHIDE_TIMEOUT_MS");
            return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0 ? value : 8000;
        }

        internal sealed class TimedResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
            public bool TimedOut { get; set; }

            public TimedResult()
            {
                Output = Error = string.Empty;
            }
        }

        internal static TimedResult RunTimed(string exe, string arguments, int timeoutMs)
        {
            TimedResult result = new TimedResult();
            ProcessStartInfo info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                result.ExitCode = -1;
                result.Error = ex.Message;
                return result;
            }
            using (process)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
                System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    result.ExitCode = HidHideCli.TimedOut;
                    KillTree(process.Id);
                    process.WaitForExit(3000);
                    result.Output = output.Wait(500) ? output.Result : string.Empty;
                    return result;
                }
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                result.Output = output.Wait(2000) ? output.Result : string.Empty;
                result.Error = error.Wait(2000) ? error.Result : string.Empty;
                return result;
            }
        }

        // Ends a process and every process it started (cmd wrappers in the tests; HidHideCLI starts none).
        internal static void KillTree(int pid)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + pid.ToString(CultureInfo.InvariantCulture)))
                using (System.Management.ManagementObjectCollection children = searcher.Get())
                {
                    foreach (System.Management.ManagementObject child in children)
                    {
                        using (child)
                            KillTree(Convert.ToInt32(child["ProcessId"], CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Management.ManagementException || ex is System.Runtime.InteropServices.COMException || ex is UnauthorizedAccessException)
            {
            }
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
            }
        }

        // The real HidHideCLI.exe: one query per call (combined queries can hang without administrator rights), each
        // with a time limit; changes only in the elevated step.
        internal sealed class HidHideCliProcess : IHidHideCli
        {
            private static readonly string[][] Queries =
            {
                new[] { HidHideState.PartCloak, "--cloak-state" },
                new[] { HidHideState.PartInverse, "--inv-state" },
                new[] { HidHideState.PartApps, "--app-list" },
                new[] { HidHideState.PartDevices, "--dev-list" }
            };

            private readonly string path;

            public HidHideCliProcess(string path, bool elevated)
            {
                this.path = path;
                Elevated = elevated;
            }

            public bool Elevated { get; private set; }

            public bool Installed
            {
                get { return File.Exists(path); }
            }

            public string Location
            {
                get { return path; }
            }

            public HidHideRead Read()
            {
                HidHideRead read = new HidHideRead();
                if (!Installed)
                {
                    foreach (string part in HidHideState.Parts)
                        read.State.SetUnknown(part);
                    read.Problems.Add("HidHide is not installed");
                    return read;
                }
                string blocked = OtherInstances();
                foreach (string[] query in Queries)
                {
                    if (blocked.Length > 0)
                    {
                        read.State.SetUnknown(query[0]);
                        continue;
                    }
                    TimedResult answer = Query(query[1]);
                    string problem;
                    if (answer.TimedOut)
                    {
                        read.State.SetUnknown(query[0]);
                        read.Problems.Add(query[1] + ": no answer within " + ReadTimeoutMs().ToString(CultureInfo.InvariantCulture) + " ms (ended)");
                    }
                    else if (!HidHideState.TryParsePart(query[0], answer.Output + (answer.Error.Length > 0 ? "\n" + answer.Error : string.Empty), read.State, out problem))
                    {
                        read.Problems.Add(query[1] + ": " + problem + (query[0] == HidHideState.PartDevices && !Elevated ? " (needs administrator rights)" : string.Empty));
                    }
                }
                if (blocked.Length > 0)
                    read.Problems.Add(blocked);
                return read;
            }

            public TimedResult Query(string flag)
            {
                return RunTimed(path, flag, ReadTimeoutMs());
            }

            public int Apply(IList<string> arguments)
            {
                if (!Elevated)
                    return -5; // never without the elevated step
                if (OtherInstances().Length > 0)
                    return -6;
                TimedResult answer = RunTimed(path, Elevation.Quote(arguments), Math.Max(30000, ReadTimeoutMs() * 3));
                return answer.TimedOut ? HidHideCli.TimedOut : answer.ExitCode;
            }

            // A HidHideCLI that still runs (hung) blocks every other call ("Access is denied"); JoyPro does not end
            // one it did not start.
            private string OtherInstances()
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), "HidHideCLI", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                List<int> pids = new List<int>();
                foreach (Process process in Process.GetProcessesByName("HidHideCLI"))
                {
                    pids.Add(process.Id);
                    process.Dispose();
                }
                return pids.Count == 0 ? string.Empty : "another HidHideCLI is still running (pid " + string.Join(", ", pids) + ") and blocks HidHide; end it in Task Manager and try again";
            }
        }

        // The fake HidHide of JOYPRO_VJOY_FAKE: state in hidhide-state.txt, every Apply appended to hidhide-calls.txt;
        // hidhide-missing (file) = not installed, hidhide-exit.txt = the exit code Apply returns (-1460 = hung),
        // hidhide-deny.txt = parts (cloak, inverse, devices, apps) denied without administrator rights,
        // hidhide-hang.txt = parts whose query hangs.
        private sealed class LoggingFakeHidHide : IHidHideCli
        {
            private readonly string folder;
            private readonly bool elevated;
            private readonly FakeHidHideCli inner;

            public LoggingFakeHidHide(string folder, bool elevated)
            {
                this.folder = folder;
                this.elevated = elevated;
                inner = new FakeHidHideCli(Path.Combine(folder, "hidhide-state.txt"));
                foreach (string part in ReadParts("hidhide-hang.txt"))
                    inner.Hanging.Add(part);
                if (!elevated)
                {
                    foreach (string part in ReadParts("hidhide-deny.txt"))
                        inner.Denied.Add(part);
                }
            }

            public bool Installed
            {
                get { return !File.Exists(Path.Combine(folder, "hidhide-missing")); }
            }

            public string Location
            {
                get { return inner.Location; }
            }

            public HidHideRead Read()
            {
                return inner.Read();
            }

            public int Apply(IList<string> arguments)
            {
                if (!elevated)
                    return -5;
                string exitPath = Path.Combine(folder, "hidhide-exit.txt");
                int exit;
                inner.ApplyExitCode = File.Exists(exitPath) && int.TryParse(File.ReadAllText(exitPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out exit) ? exit : 0;
                File.AppendAllText(Path.Combine(folder, "hidhide-calls.txt"), Elevation.Quote(arguments) + "\r\n");
                return inner.Apply(arguments);
            }

            private IEnumerable<string> ReadParts(string file)
            {
                string path = Path.Combine(folder, file);
                return File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0) : Enumerable.Empty<string>();
            }
        }

        public static HidHideSession CreateSession(IHidHideCli cli)
        {
            return new HidHideSession(cli, JournalPath(), IsProcessAlive);
        }

        public static bool IsProcessAlive(int pid, long startTicks)
        {
            if (pid <= 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited)
                        return false;
                    if (startTicks == 0)
                        return true;
                    long ticks = process.StartTime.ToUniversalTime().Ticks;
                    return Math.Abs(ticks - startTicks) < TimeSpan.TicksPerSecond;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                // Running, but its start time cannot be read: count it as the owner (no restore behind its back).
                return true;
            }
        }

        public static long ProcessStartTicks(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    return process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------ elevation (one UAC prompt per call)

        internal static class Elevation
        {
            public static int Run(string exe, IList<string> arguments)
            {
                ProcessStartInfo info = new ProcessStartInfo(exe, Quote(arguments))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
                };
                try
                {
                    using (Process process = Process.Start(info))
                    {
                        if (process == null)
                            return HidHideCli.Cancelled;
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return HidHideCli.Cancelled;
                }
            }

            // Arguments quoted for CommandLineToArgvW.
            public static string Quote(IEnumerable<string> arguments)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string raw in arguments)
                {
                    string argument = raw ?? string.Empty;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                    {
                        sb.Append(argument);
                        continue;
                    }
                    sb.Append('"');
                    int backslashes = 0;
                    foreach (char c in argument)
                    {
                        if (c == '\\')
                        {
                            backslashes++;
                            continue;
                        }
                        if (c == '"')
                        {
                            sb.Append('\\', backslashes * 2 + 1);
                            sb.Append('"');
                        }
                        else
                        {
                            sb.Append('\\', backslashes);
                            sb.Append(c);
                        }
                        backslashes = 0;
                    }
                    sb.Append('\\', backslashes * 2);
                    sb.Append('"');
                }
                return sb.ToString();
            }
        }

        // vJoyConfig for the devices that need it; real: one elevated "engine vjoy elevated-run" with every call.
        private static int RunVJoyConfig(VJoyInstallInfo install, List<List<string>> invocations, List<string> warnings)
        {
            string fake = FakeFolder();
            if (fake != null)
            {
                File.AppendAllText(Path.Combine(fake, "vjoyconfig-calls.txt"), string.Join("\r\n", invocations.Select(Elevation.Quote)) + "\r\n");
                string exitPath = Path.Combine(fake, "vjoyconfig-exit.txt");
                int exit;
                if (File.Exists(exitPath) && int.TryParse(File.ReadAllText(exitPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out exit) && exit != 0)
                    return exit;
                // The fake driver takes the new configuration.
                string path = Path.Combine(fake, "vjoy.txt");
                List<string> lines = ReadFakeLines(path);
                foreach (List<string> call in invocations)
                {
                    VJoyDeviceCaps caps = CapsFromConfigArguments(call);
                    if (caps == null)
                        continue;
                    lines.RemoveAll(l => { VJoyDeviceCaps old = ParseFakeDevice(l); return old != null && old.Id == caps.Id; });
                    lines.Add(FakeDeviceLine(caps));
                }
                File.WriteAllLines(path, lines);
                return 0;
            }
            if (!File.Exists(install.ConfigTool))
            {
                warnings.Add("vJoyConfig.exe not found: " + install.ConfigTool);
                return 2;
            }
            if (invocations.Count == 1)
                return Elevation.Run(install.ConfigTool, invocations[0]);
            List<string> helper = new List<string> { "vjoy", "elevated-run" };
            for (int i = 0; i < invocations.Count; i++)
            {
                if (i > 0)
                    helper.Add("---");
                helper.Add("vjoyconfig");
                helper.AddRange(invocations[i]);
            }
            return Elevation.Run(Process.GetCurrentProcess().MainModule.FileName, helper);
        }

        private static VJoyDeviceCaps CapsFromConfigArguments(List<string> args)
        {
            int id;
            if (args.Count == 0 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return null;
            VJoyDeviceCaps caps = new VJoyDeviceCaps { Id = id, Configured = true, Axes = Enumerable.Range(0, 8).ToList(), Buttons = 8 };
            for (int i = 1; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "-a":
                        caps.Axes = new List<int>();
                        while (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            caps.Axes.Add(VJoyConfigArgs.AxisIndex(args[++i]));
                        break;
                    case "-b": caps.Buttons = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "-p": caps.ContinuousPovs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "-s": caps.DiscretePovs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "-e":
                        caps.ForceFeedback = true;
                        while (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                        break;
                }
            }
            return caps;
        }

        // engine vjoy elevated-run vjoyconfig <args> [--- vjoyconfig <args>]...: the elevated helper of configure-vjoy
        // (started with runas, one UAC prompt for several devices). Only vJoyConfig.exe of the installed vJoy is run.
        private static int RunElevatedHelper(List<string> args)
        {
            VJoyInstallInfo install = ProbeVJoy();
            if (install.Fake || !File.Exists(install.ConfigTool))
                return 2;
            List<List<string>> calls = new List<List<string>>();
            List<string> current = null;
            foreach (string arg in args)
            {
                if (arg == "---")
                {
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    if (!string.Equals(arg, "vjoyconfig", StringComparison.OrdinalIgnoreCase))
                        return 3;
                    current = new List<string>();
                    calls.Add(current);
                    continue;
                }
                current.Add(arg);
            }
            foreach (List<string> call in calls)
            {
                ProcessStartInfo info = new ProcessStartInfo(install.ConfigTool, Elevation.Quote(call)) { UseShellExecute = false, CreateNoWindow = true };
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        return process.ExitCode;
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ the games' executables (HidHide block list)

        private static string ExesPath()
        {
            return Path.Combine(DataFolder(), "game-exes.json");
        }

        private static Dictionary<string, List<string>> LoadExtraExes()
        {
            try
            {
                string path = ExesPath();
                if (!File.Exists(path))
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<string>> loaded = EngineSerialization.FromJson<Dictionary<string, List<string>>>(File.ReadAllText(path));
                return new Dictionary<string, List<string>>(loaded ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveExtraExes(Dictionary<string, List<string>> exes)
        {
            Directory.CreateDirectory(DataFolder());
            File.WriteAllText(ExesPath(), EngineSerialization.ToJson(exes), new UTF8Encoding(false));
        }

        // The game's executables: the ones set with engine vjoy exe, the installation's marker executables and the
        // game's process names found in the installation folder (a few levels deep).
        public static List<string> GameExes(string gameId, List<string> warnings)
        {
            List<string> output = new List<string>();
            List<string> extra;
            if (LoadExtraExes().TryGetValue(gameId, out extra))
                foreach (string exe in extra)
                    AddExe(output, exe);
            GameInstall install = GameInstalls.Find(gameId, warnings);
            if (install != null && install.Installed && Directory.Exists(install.InstallPath))
            {
                foreach (string marker in install.Markers.Where(m => m.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    string candidate = Path.Combine(install.InstallPath, marker);
                    string name = Path.GetFileNameWithoutExtension(candidate);
                    if (ProcessNames(gameId).Contains(name, StringComparer.OrdinalIgnoreCase))
                        AddExe(output, candidate);
                }
                foreach (string name in ProcessNames(gameId))
                {
                    foreach (string found in FindFiles(install.InstallPath, name + ".exe", 4, 3000))
                        AddExe(output, found);
                }
            }
            return output;
        }

        private static void AddExe(List<string> output, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return;
            }
            if (File.Exists(full) && !output.Contains(full, StringComparer.OrdinalIgnoreCase))
                output.Add(full);
        }

        private static IEnumerable<string> FindFiles(string root, string fileName, int depth, int maxFolders)
        {
            List<string> found = new List<string>();
            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < maxFolders)
            {
                KeyValuePair<string, int> folder = queue.Dequeue();
                visited++;
                try
                {
                    string candidate = Path.Combine(folder.Key, fileName);
                    if (File.Exists(candidate))
                        found.Add(candidate);
                    if (folder.Value >= depth)
                        continue;
                    foreach (string sub in Directory.EnumerateDirectories(folder.Key))
                        queue.Enqueue(new KeyValuePair<string, int>(sub, folder.Value + 1));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            return found;
        }

        // ------------------------------------------------------------------ the export / import scope

        // While a game with virtual mode is exported, imported or synced, the profile's binds point at the vJoy slots
        // (device, input and modifier buttons); afterwards everything is put back. Binds an import adds on vJoy slots
        // are turned back into the physical inputs. The table grows first if the game uses an input without a slot.
        internal sealed class GameScope : IDisposable
        {
            private readonly List<KeyValuePair<Bind, string[]>> originals = new List<KeyValuePair<Bind, string[]>>();
            private Dictionary<string, Modifier> originalModifiers;
            private Dictionary<string, KeyValuePair<int, string>> forward;
            private Dictionary<string, KeyValuePair<string, string>> backward;
            private bool reverseAfter;

            public string Error { get; private set; }
            public bool MappingChanged { get; private set; }
            public string Note { get; private set; }

            public static GameScope None()
            {
                return new GameScope();
            }

            public static GameScope Begin(string gameId, string action)
            {
                GameScope scope = new GameScope();
                if (string.IsNullOrWhiteSpace(gameId))
                    return scope;
                VJoySettings settings = LoadSettings();
                if (settings.IsXbox(gameId))
                    return BeginXbox(settings, gameId, action);
                if (!settings.IsVirtual(gameId))
                    return scope;

                Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
                VJoyPlan plan = BuildPlan(settings, caps, false, false);
                if (plan.Errors.Count > 0)
                {
                    scope.Error = "Virtual joystick mode for " + GameName(gameId) + ": " + string.Join(" ", plan.Errors);
                    return scope;
                }
                if (plan.Changed)
                {
                    settings.Slots = plan.Slots;
                    SaveSettings(settings);
                    scope.MappingChanged = true;
                }
                // Only the vJoy devices' DirectInput names are needed here (no HidHide call).
                DeviceScan scan = ScanDevices(null, caps);
                List<int> devices = plan.Slots.Select(s => s.Device).Distinct().OrderBy(d => d).ToList();
                List<int> missing = devices.Where(d => string.IsNullOrEmpty(scan.NameOf(d))).ToList();
                if (missing.Count > 0)
                {
                    scope.Error = "Virtual joystick mode for " + GameName(gameId) + ": vJoy device(s) " + string.Join(", ", missing) +
                                  " are not there (configure vJoy: engine vjoy configure-vjoy), or switch the game back to native (engine vjoy mode " + gameId + " native).";
                    return scope;
                }
                scope.forward = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
                scope.backward = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (VJoySlot slot in plan.Slots)
                {
                    string name = scan.NameOf(slot.Device);
                    scope.forward[VJoyPlanner.Key(slot.Source, slot.Input)] = new KeyValuePair<int, string>(slot.Device, slot.Target);
                    scope.backward[VJoyPlanner.Key(name, slot.Target)] = new KeyValuePair<string, string>(slot.Source, slot.Input);
                }
                Dictionary<int, string> names = devices.ToDictionary(d => d, scan.NameOf);
                scope.SwapForward(names);
                scope.reverseAfter = action == "import" || action == "apply" || action == "accept";
                scope.Note = "Virtual joystick mode: " + GameName(gameId) + " uses vJoy device(s) " + string.Join(", ", devices) + " (" + plan.Slots.Count.ToString(CultureInfo.InvariantCulture) + " slot(s)).";
                return scope;
            }

            // Virtual Xbox pad: games that store gamepad binds get them on EngineVJoy.XboxDeviceName (XB_* inputs);
            // for the other games nothing is swapped (the game's own gamepad layout applies, the table follows it).
            private static GameScope BeginXbox(VJoySettings settings, string gameId, string action)
            {
                GameScope scope = new GameScope();
                List<string> warnings = new List<string>();
                XboxPlan plan = BuildXboxPlans(settings, false, false, warnings).First(x => string.Equals(x.Game, gameId, StringComparison.OrdinalIgnoreCase));
                if (plan.Changed)
                {
                    ApplyXboxPlans(settings, new List<XboxPlan> { plan });
                    SaveSettings(settings);
                    scope.MappingChanged = true;
                }
                if (!WritesGamepadBinds(gameId))
                {
                    scope.Note = "Virtual Xbox pad: " + GameName(gameId) + " keeps its own gamepad layout; JoyPro writes no gamepad binds for it (" + plan.Slots.Count.ToString(CultureInfo.InvariantCulture) + " slot(s)).";
                    return scope;
                }
                scope.xboxForward = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                scope.xboxBackward = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (VJoySlot slot in plan.Slots)
                {
                    scope.xboxForward[VJoyPlanner.Key(slot.Source, slot.Input)] = slot.Target;
                    if (!scope.xboxBackward.ContainsKey(slot.Target))
                        scope.xboxBackward[slot.Target] = new KeyValuePair<string, string>(slot.Source, slot.Input);
                }
                scope.forward = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
                scope.SwapForward(new Dictionary<int, string>());
                scope.reverseAfter = action == "import" || action == "apply" || action == "accept";
                scope.Note = "Virtual Xbox pad: " + GameName(gameId) + " gets its binds on the virtual Xbox 360 pad (" + plan.Slots.Count.ToString(CultureInfo.InvariantCulture) + " slot(s)).";
                return scope;
            }

            private void SwapForward(Dictionary<int, string> names)
            {
                foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds)
                {
                    Bind bind = entry.Value;
                    if (bind == null)
                        continue;
                    Relation relation;
                    InternalDataManagement.AllRelations.TryGetValue(entry.Key, out relation);
                    bool axis = relation != null ? relation.ISAXIS : !string.IsNullOrWhiteSpace(bind.JAxis) && string.IsNullOrWhiteSpace(bind.JButton);
                    string device;
                    string token;
                    bool inputMapped = TryMap(bind.Joystick, axis ? bind.JAxis : bind.JButton, names, out device, out token);
                    List<string> reformers = MapReformers(bind.AllReformers, names);
                    if (!inputMapped && reformers == null)
                        continue;
                    originals.Add(new KeyValuePair<Bind, string[]>(bind, new[] { bind.Joystick, bind.JAxis, bind.JButton }));
                    originalReformers.Add(new KeyValuePair<Bind, List<string>>(bind, bind.AllReformers));
                    if (inputMapped)
                    {
                        bind.Joystick = device;
                        if (axis)
                            bind.JAxis = token;
                        else
                            bind.JButton = token;
                    }
                    if (reformers != null)
                        bind.AllReformers = reformers;
                }
                originalModifiers = InternalDataManagement.AllModifiers;
                Dictionary<string, Modifier> modifiers = new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Modifier> entry in originalModifiers ?? new Dictionary<string, Modifier>())
                {
                    Modifier copy = entry.Value == null ? null : entry.Value.Copy();
                    string device;
                    string token;
                    if (copy != null && TryMap(copy.device, copy.key, names, out device, out token))
                    {
                        copy.device = device;
                        copy.key = token;
                    }
                    modifiers[entry.Key] = copy;
                }
                InternalDataManagement.AllModifiers = modifiers;
            }

            private readonly List<KeyValuePair<Bind, List<string>>> originalReformers = new List<KeyValuePair<Bind, List<string>>>();

            private List<string> MapReformers(List<string> reformers, Dictionary<int, string> names)
            {
                if (reformers == null || reformers.Count == 0)
                    return null;
                bool any = false;
                List<string> output = new List<string>();
                foreach (string reformer in reformers)
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    string device;
                    string token;
                    if (modifier != null && TryMap(modifier.device, modifier.key, names, out device, out token))
                    {
                        modifier.device = device;
                        modifier.key = token;
                        output.Add(modifier.toReformerString());
                        any = true;
                    }
                    else
                    {
                        output.Add(reformer);
                    }
                }
                return any ? output : null;
            }

            private bool TryMap(string device, string token, Dictionary<int, string> names, out string vjoyDevice, out string vjoyToken)
            {
                vjoyDevice = vjoyToken = null;
                VJoyInput input;
                int direction;
                if (string.IsNullOrWhiteSpace(device) || !VJoyInput.TryFromBindToken(token, out input, out direction))
                    return false;
                if (xboxForward != null)
                {
                    string xb;
                    if (!xboxForward.TryGetValue(VJoyPlanner.Key(device.Trim(), input.ToSlotToken()), out xb))
                        return false;
                    vjoyDevice = XboxDeviceName;
                    vjoyToken = input.Kind == VJoyInputKind.Pov ? DPadDirection(direction) : xb;
                    return vjoyToken.Length > 0;
                }
                KeyValuePair<int, string> slot;
                if (!forward.TryGetValue(VJoyPlanner.Key(device.Trim(), input.ToSlotToken()), out slot))
                    return false;
                VJoyInput target;
                if (!VJoyInput.TryParseSlotToken(slot.Value, out target))
                    return false;
                vjoyDevice = names[slot.Key];
                vjoyToken = target.ToBindToken(direction);
                return true;
            }

            // Hat directions (0 U .. 7 UL) on the D-pad: diagonals take their first part.
            private static readonly string[] DPadTokens = { "XB_DPAD_UP", "XB_DPAD_RIGHT", "XB_DPAD_DOWN", "XB_DPAD_LEFT" };

            private static string DPadDirection(int direction)
            {
                return direction < 0 ? string.Empty : DPadTokens[(direction / 2) % 4];
            }

            private Dictionary<string, string> xboxForward;
            private Dictionary<string, KeyValuePair<string, string>> xboxBackward;

            private bool TryMapBack(string device, string token, out string physicalDevice, out string physicalToken)
            {
                physicalDevice = physicalToken = null;
                VJoyInput input;
                int direction;
                if (xboxBackward != null && string.Equals((device ?? string.Empty).Trim(), XboxDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    string target = (token ?? string.Empty).Trim().ToUpperInvariant();
                    int hatDirection = Array.IndexOf(DPadTokens, target);
                    KeyValuePair<string, string> xslot;
                    if (!xboxBackward.TryGetValue(hatDirection >= 0 ? XboxControls.DPad : target, out xslot) && !xboxBackward.TryGetValue(target, out xslot))
                        return false;
                    VJoyInput physical;
                    if (!VJoyInput.TryParseSlotToken(xslot.Value, out physical))
                        return false;
                    physicalDevice = xslot.Key;
                    physicalToken = physical.Kind == VJoyInputKind.Pov ? physical.ToBindToken(hatDirection < 0 ? 0 : hatDirection * 2) : physical.ToBindToken(-1);
                    return true;
                }
                if (backward == null || string.IsNullOrWhiteSpace(device) || !VJoyInput.TryFromBindToken(token, out input, out direction))
                    return false;
                KeyValuePair<string, string> slot;
                if (!backward.TryGetValue(VJoyPlanner.Key(device.Trim(), input.ToSlotToken()), out slot))
                    return false;
                VJoyInput source;
                if (!VJoyInput.TryParseSlotToken(slot.Value, out source))
                    return false;
                physicalDevice = slot.Key;
                physicalToken = source.ToBindToken(direction);
                return true;
            }

            public void Dispose()
            {
                foreach (KeyValuePair<Bind, string[]> entry in originals)
                {
                    entry.Key.Joystick = entry.Value[0];
                    entry.Key.JAxis = entry.Value[1];
                    entry.Key.JButton = entry.Value[2];
                }
                foreach (KeyValuePair<Bind, List<string>> entry in originalReformers)
                    entry.Key.AllReformers = entry.Value;
                originals.Clear();
                originalReformers.Clear();
                if (originalModifiers != null)
                    InternalDataManagement.AllModifiers = originalModifiers;
                originalModifiers = null;
                if (!reverseAfter)
                    return;
                reverseAfter = false;
                // Binds the import put on vJoy slots: back to the physical inputs.
                foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds)
                {
                    Bind bind = entry.Value;
                    if (bind == null)
                        continue;
                    Relation relation;
                    InternalDataManagement.AllRelations.TryGetValue(entry.Key, out relation);
                    bool axis = relation != null ? relation.ISAXIS : !string.IsNullOrWhiteSpace(bind.JAxis);
                    string device;
                    string token;
                    if (TryMapBack(bind.Joystick, axis ? bind.JAxis : bind.JButton, out device, out token))
                    {
                        bind.Joystick = device;
                        if (axis)
                            bind.JAxis = token;
                        else
                            bind.JButton = token;
                    }
                    if (bind.AllReformers == null)
                        continue;
                    for (int i = 0; i < bind.AllReformers.Count; i++)
                    {
                        Modifier modifier = Modifier.ReformerToMod(bind.AllReformers[i]);
                        if (modifier != null && TryMapBack(modifier.device, modifier.key, out device, out token))
                        {
                            modifier.device = device;
                            modifier.key = token;
                            bind.AllReformers[i] = modifier.toReformerString();
                        }
                    }
                }
                InternalDataManagement.ResyncBindsToMods();
            }
        }

        // The game a command of the game areas works on, when it reads or writes the game's binds.
        public static GameScope BeginGameCommand(string gameId, string area, List<string> command)
        {
            string action = command == null || command.Count == 0 ? string.Empty : (command[0] ?? string.Empty).Trim().ToLowerInvariant();
            if (string.Equals(area, "sync", StringComparison.OrdinalIgnoreCase))
            {
                if (action != "status" && action != "diff" && action != "apply" && action != "accept")
                    return GameScope.None();
                int index = command.FindIndex(c => string.Equals(c, "--game", StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index + 1 >= command.Count)
                    return GameScope.None();
                gameId = GameInstalls.Resolve(command[index + 1]);
            }
            else if (action != "export" && action != "import" && action != "scan")
            {
                return GameScope.None();
            }
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(InternalDataManagement.VirtualJoystickConfig))
                return GameScope.None();
            return GameScope.Begin(gameId, action);
        }

        // ------------------------------------------------------------------ CLI

        public static bool IsMutating(List<string> command)
        {
            string action = command == null || command.Count == 0 ? "status" : (command[0] ?? string.Empty).Trim().ToLowerInvariant();
            bool dry = command != null && command.Any(c => string.Equals(c, "--dry-run", StringComparison.OrdinalIgnoreCase));
            if (action == "functions")
            {
                string sub = command.Count > 1 ? (command[1] ?? string.Empty).Trim().ToLowerInvariant() : "list";
                return !dry && (sub == "add" || sub == "set" || sub == "remove");
            }
            return !dry && (action == "mode" || action == "map" || action == "settings");
        }

        public static int Run(List<string> command, bool textOutput, out string payload)
        {
            payload = string.Empty;
            List<string> args = command.Skip(1).ToList();
            string action = command.Count == 0 ? "status" : command[0].Trim().ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "status":
                        return Status(args, textOutput, out payload);
                    case "mode":
                        return Mode(args, textOutput, out payload);
                    case "plan":
                        return MapCommand(args, true, textOutput, out payload);
                    case "map":
                        return MapCommand(args, false, textOutput, out payload);
                    case "settings":
                        return Settings(args, textOutput, out payload);
                    case "exe":
                        return Exe(args, textOutput, out payload);
                    case "forwarder-config":
                        return ForwarderConfig(args, textOutput, out payload);
                    case "functions":
                        return FunctionsCommand(args, textOutput, out payload);
                    case "limits":
                        return LimitsCommand(args, textOutput, out payload);
                    case "configure-vjoy":
                        return ConfigureVJoy(args, textOutput, out payload);
                    case "apply-hidhide":
                        return ApplyHidHide(args, textOutput, out payload);
                    case "restore-hidhide":
                        return RestoreHidHide(args, false, textOutput, out payload);
                    case "recover":
                        return RestoreHidHide(args, true, textOutput, out payload);
                    case "elevated-hidhide":
                        return RunElevatedStep(args);
                    case "elevated-run":
                        return RunElevatedHelper(args);
                    default:
                        payload = "Unknown vjoy action: " + action + ". Use status, mode, plan, map, settings, exe, functions, limits, forwarder-config, configure-vjoy, apply-hidhide, restore-hidhide or recover.";
                        return 1;
                }
            }
            catch (ArgumentException ex)
            {
                payload = ex.Message;
                return 1;
            }
        }

        private static string PopValue(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return null;
            if (index + 1 >= args.Count)
                throw new ArgumentException("Missing value for " + name + ".");
            string value = args[index + 1];
            args.RemoveRange(index, 2);
            return value;
        }

        private static List<string> PopValues(List<string> args, string name)
        {
            List<string> values = new List<string>();
            string value;
            while ((value = PopValue(args, name)) != null)
                values.Add(value);
            return values;
        }

        private static bool PopFlag(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;
            args.RemoveAt(index);
            return true;
        }

        private static void RefuseLeftovers(List<string> args, string action)
        {
            if (args.Count > 0)
                throw new ArgumentException("Unknown vjoy " + action + " argument(s): " + string.Join(" ", args));
        }

        private static bool? ParseOnOff(string value, string name)
        {
            if (value == null)
                return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "yes": case "1": return true;
                case "off": case "false": case "no": case "0": return false;
            }
            throw new ArgumentException(name + " takes on or off.");
        }

        // ---- status

        private static int Status(List<string> args, bool textOutput, out string payload)
        {
            RefuseLeftovers(args, "status");
            Dictionary<string, object> status = BuildStatus();
            payload = textOutput ? StatusText(status) : EngineSerialization.ToJson(status);
            return 0;
        }

        internal static Dictionary<string, object> BuildStatus()
        {
            List<string> warnings = new List<string>();
            VJoySettings settings = LoadSettings();
            VJoyInstallInfo vjoy = ProbeVJoy();
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            IHidHideCli hidHide = CreateHidHide();
            // Read-only and without administrator rights: one query per part, each with a time limit; the hidden-device
            // list is usually denied then (shown as unknown, never as empty).
            HidHideRead hidRead = hidHide.Installed ? hidHide.Read() : new HidHideRead();
            HidHideState hidState = hidRead.State;
            DeviceScan scan = ScanDevices(null, caps);
            warnings.AddRange(scan.Warnings);
            VJoyPlan plan = BuildPlan(settings, caps, false, false);
            HidHideSession session = CreateSession(hidHide);
            HidHideJournal journal = session.LoadJournal();
            List<VJoyUsedInput> used = CollectUsedInputs(settings.VirtualGames());
            Dictionary<string, VJoyUsedInput> usedByKey = used.ToDictionary(u => VJoyPlanner.Key(u.Source, u.Input), StringComparer.OrdinalIgnoreCase);
            HashSet<string> added = new HashSet<string>(plan.Added.Select(s => VJoyPlanner.Key(s.Source, s.Input)), StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<string>> limitWarnings = LimitWarnings(settings, plan, used);
            List<object> games = new List<object>();
            foreach (string id in GameIds())
            {
                int relations = InternalDataManagement.AllRelations.Values.Count(r => r != null && r.NODES != null && r.NODES.Any(n => n != null && NodeMatches(id, n.Game)));
                List<string> gameLimitWarnings;
                if (!limitWarnings.TryGetValue(id, out gameLimitWarnings))
                    gameLimitWarnings = new List<string>();
                games.Add(new Dictionary<string, object>
                {
                    { "Id", id },
                    { "Name", GameName(id) },
                    { "Mode", settings.IsVirtual(id) ? "virtual" : settings.IsXbox(id) ? "xbox" : "native" },
                    { "Relations", relations },
                    { "ProcessNames", ProcessNames(id) },
                    { "Exes", settings.IsAnyVirtual(id) ? GameExes(id, warnings) : new List<string>() },
                    { "GamepadLayout", GamepadLayout(id, new List<string>()).Count },
                    { "WritesGamepadBinds", WritesGamepadBinds(id) },
                    { "SuggestedMode", SuggestedMode(id) },
                    { "MaxJoysticks", DeviceLimit(id) == null ? -1 : DeviceLimit(id).MaxJoysticks },
                    { "DeviceLimitNote", DeviceLimit(id) == null ? string.Empty : DeviceLimit(id).DeviceLimitNote ?? string.Empty },
                    { "InputLimit", LimitItem(id) },
                    { "LimitWarnings", gameLimitWarnings },
                    { "Functions", settings.Functions != null && settings.Functions.ContainsKey(id) ? settings.Functions[id].Count : 0 }
                });
            }

            // The virtual Xbox pad: per game its table, what does not fit and the notes.
            List<XboxPlan> xboxPlans = BuildXboxPlans(settings, false, false, warnings);
            List<object> xboxMapping = new List<object>();
            List<object> xboxGames = new List<object>();
            foreach (XboxPlan xp in xboxPlans)
            {
                HashSet<string> xAdded = new HashSet<string>(xp.Added.Select(s => VJoyPlanner.Key(s.Source, s.Input)), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, XboxUse> uses = CollectXboxUses(xp.Game, GamepadLayout(xp.Game, new List<string>())).ToDictionary(u => VJoyPlanner.Key(u.Source, u.Input), StringComparer.OrdinalIgnoreCase);
                foreach (VJoySlot slot in xp.Slots)
                {
                    string key = VJoyPlanner.Key(slot.Source, slot.Input);
                    XboxUse use;
                    uses.TryGetValue(key, out use);
                    string name;
                    Guid guid;
                    GameDeviceDirectory.TryParseJoyProName(slot.Source, out name, out guid);
                    xboxMapping.Add(new Dictionary<string, object>
                    {
                        { "Game", xp.Game },
                        { "Source", slot.Source },
                        { "SourceName", name },
                        { "Input", slot.Input },
                        { "Target", slot.Target },
                        { "TargetName", XboxControls.Describe(slot.Target) },
                        { "Pinned", use != null && string.Equals(use.Pinned, slot.Target, StringComparison.OrdinalIgnoreCase) },
                        { "Action", use == null ? string.Empty : use.PinnedAction },
                        { "Connected", scan.Find(slot.Source) != null },
                        { "Used", use != null },
                        { "New", xAdded.Contains(key) },
                        { "Relations", use == null ? new List<string>() : use.Relations }
                    });
                }
                xboxGames.Add(new Dictionary<string, object> { { "Game", xp.Game }, { "Slots", xp.Slots.Count }, { "Unfit", xp.Unfit }, { "Notes", xp.Warnings } });
            }
            ViGEmInfo vigem = ProbeViGEm();

            List<object> mapping = new List<object>();
            foreach (VJoySlot slot in plan.Slots)
            {
                string key = VJoyPlanner.Key(slot.Source, slot.Input);
                VJoyUsedInput item;
                usedByKey.TryGetValue(key, out item);
                GameDevice device = scan.Find(slot.Source);
                string name;
                Guid guid;
                GameDeviceDirectory.TryParseJoyProName(slot.Source, out name, out guid);
                mapping.Add(new Dictionary<string, object>
                {
                    { "Source", slot.Source },
                    { "SourceName", name },
                    { "Input", slot.Input },
                    { "Device", slot.Device },
                    { "Target", slot.Target },
                    { "Connected", device != null },
                    { "Used", item != null },
                    { "New", added.Contains(key) },
                    { "Relations", item == null ? new List<string>() : item.Relations },
                    { "Games", item == null ? new List<string>() : item.Games }
                });
            }

            List<object> vjoyDevices = new List<object>();
            foreach (VJoyDeviceCaps device in caps.Values.OrderBy(c => c.Id))
            {
                VJoyPresence presence = scan.VJoy.FirstOrDefault(v => v.Id == device.Id);
                if (!device.Configured && presence == null)
                    continue;
                vjoyDevices.Add(new Dictionary<string, object>
                {
                    { "Id", device.Id },
                    { "Configured", device.Configured },
                    { "Axes", device.Axes.Select(VJoyConfigArgs.AxisName).ToList() },
                    { "Buttons", device.Buttons },
                    { "Povs", device.ContinuousPovs },
                    { "DiscretePovs", device.DiscretePovs },
                    { "Describe", device.Describe() },
                    { "AtMaximum", VJoyPlanner.AtMaximum(device) },
                    { "DirectInputName", presence == null ? string.Empty : presence.DeviceName },
                    { "InstancePath", presence == null ? string.Empty : presence.InstancePath }
                });
            }

            List<object> needs = plan.Devices.Select(n => (object)new Dictionary<string, object>
            {
                { "Id", n.Id },
                { "Slots", n.Slots },
                { "FunctionOutputs", n.FunctionOutputs },
                { "Sufficient", n.Sufficient },
                { "Missing", n.Missing },
                { "Current", n.Current },
                { "Recommended", n.Recommended },
                { "ConfigCommand", n.ConfigArguments.Count == 0 ? string.Empty : "vJoyConfig " + Elevation.Quote(n.ConfigArguments) }
            }).ToList();

            List<string> hiddenSources = plan.Slots.Select(s => s.Source).Concat(xboxPlans.SelectMany(x => x.Slots).Select(s => s.Source)).Concat(FunctionSources(settings)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<object> physical = new List<object>();
            foreach (string source in hiddenSources)
            {
                GameDevice device = scan.Find(source);
                if (device != null)
                    scan.Directory.EnsureCapabilities(device, warnings);
                string instance = device == null ? string.Empty : scan.InstancePathOf(device.HidPath);
                physical.Add(new Dictionary<string, object>
                {
                    { "Device", source },
                    { "Connected", device != null },
                    { "InstancePath", instance },
                    { "HiddenNow", instance.Length > 0 && !hidState.DevicesUnknown && hidState.HasDevice(instance) && hidState.CloakOn }
                });
            }

            Dictionary<string, object> sessionInfo = new Dictionary<string, object>
            {
                { "Phase", journal == null ? "none" : journal.Phase },
                { "Stale", journal != null && session.IsStale(journal) },
                { "OwnerPid", journal == null ? 0 : journal.OwnerPid },
                { "Strategy", journal == null ? string.Empty : (journal.Strategy == HidHideStrategy.AllowList ? "allow-list" : "block-list") },
                { "Games", journal == null ? new List<string>() : journal.Games },
                { "AddedDevices", journal == null ? new List<string>() : journal.AddedDevices },
                { "AddedApps", journal == null ? new List<string>() : journal.AddedApps },
                { "RemovedDevices", journal == null ? new List<string>() : journal.RemovedDevices },
                { "RemovedApps", journal == null ? new List<string>() : journal.RemovedApps },
                { "JournalPath", session.JournalPath }
            };

            return new Dictionary<string, object>
            {
                { "VJoy", new Dictionary<string, object>
                    {
                        { "Installed", vjoy.Installed },
                        { "Version", vjoy.Version },
                        { "Folder", vjoy.Folder },
                        { "InterfaceDll", vjoy.InterfaceDll },
                        { "ConfigTool", vjoy.ConfigTool },
                        { "Fake", vjoy.Fake },
                        { "Devices", vjoyDevices }
                    }
                },
                { "HidHide", new Dictionary<string, object>
                    {
                        { "Installed", hidHide.Installed },
                        { "Location", hidHide.Location },
                        { "Version", HidHideVersion() },
                        { "CloakOn", hidState.CloakOn },
                        { "InverseOn", hidState.InverseOn },
                        { "Devices", hidState.Devices },
                        { "Apps", hidState.Apps },
                        { "Unreadable", hidState.UnknownParts() },
                        { "ReadProblems", hidRead.Problems }
                    }
                },
                { "Session", sessionInfo },
                { "Settings", new Dictionary<string, object>
                    {
                        { "FirstDevice", settings.FirstDevice },
                        { "LastDevice", settings.LastDevice },
                        { "AutoStart", settings.AutoStart },
                        { "HideDevices", settings.HideDevices }
                    }
                },
                { "ViGEm", new Dictionary<string, object>
                    {
                        { "Installed", vigem.Installed },
                        { "Running", vigem.Running },
                        { "Version", vigem.Version },
                        { "Fake", vigem.Fake },
                        { "InstallerUrl", ViGEmInstallerUrl }
                    }
                },
                { "Games", games },
                { "Functions", FunctionItems(settings, plan, caps, scan, string.Empty) },
                { "InputDevices", InputDevices(scan) },
                { "Mapping", mapping },
                { "XboxMapping", xboxMapping },
                { "XboxGames", xboxGames },
                { "Needs", needs },
                { "PhysicalDevices", physical },
                { "MappingChanged", plan.Changed },
                { "RefreshRateHz", DisplayRefresh.HighestRateHz() },
                { "Errors", plan.Errors },
                { "Warnings", warnings.Concat(plan.Warnings).Distinct().ToList() }
            };
        }

        internal static string StatusText(Dictionary<string, object> status)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, object> vjoy = (Dictionary<string, object>)status["VJoy"];
            Dictionary<string, object> hid = (Dictionary<string, object>)status["HidHide"];
            Dictionary<string, object> session = (Dictionary<string, object>)status["Session"];
            sb.AppendLine("Virtual Joystick (vJoy + HidHide)");
            sb.AppendLine("vJoy: " + ((bool)vjoy["Installed"] ? "installed " + vjoy["Version"] : "not installed") + ((bool)vjoy["Fake"] ? " (fake)" : string.Empty));
            foreach (Dictionary<string, object> device in ((List<object>)vjoy["Devices"]).Cast<Dictionary<string, object>>())
                sb.AppendLine("  vJoy device " + device["Id"] + ": " + device["Describe"] + (((string)device["DirectInputName"]).Length > 0 ? " | " + device["DirectInputName"] : " | not seen by DirectInput"));
            List<string> unreadable = (List<string>)hid["Unreadable"];
            sb.AppendLine("HidHide: " + ((bool)hid["Installed"]
                ? "installed " + hid["Version"] +
                  ", cloaking " + (unreadable.Contains("cloaking state") ? "unknown" : (bool)hid["CloakOn"] ? "on" : "off") +
                  ", " + (unreadable.Contains("inverse mode") ? "inverse mode unknown" : (bool)hid["InverseOn"] ? "inverse (block list)" : "allow list") +
                  ", " + (unreadable.Contains("hidden devices") ? "hidden devices unknown" : ((List<string>)hid["Devices"]).Count + " hidden device(s)") +
                  ", " + (unreadable.Contains("program list") ? "programs unknown" : ((List<string>)hid["Apps"]).Count + " program(s)")
                : "not installed"));
            foreach (string problem in (List<string>)hid["ReadProblems"])
                sb.AppendLine("  not read: " + problem);
            Dictionary<string, object> vigem = (Dictionary<string, object>)status["ViGEm"];
            sb.AppendLine("ViGEmBus (virtual Xbox pad): " + ((bool)vigem["Installed"] ? "installed " + vigem["Version"] + ((bool)vigem["Running"] ? string.Empty : " (not running)") + ((bool)vigem["Fake"] ? " (fake)" : string.Empty) : "not installed (" + vigem["InstallerUrl"] + ")"));
            sb.AppendLine("Session: " + session["Phase"] + ((bool)session["Stale"] ? " (STALE: run engine vjoy recover)" : string.Empty));
            sb.AppendLine("Refresh rate: " + status["RefreshRateHz"] + " Hz");
            sb.AppendLine("Games:");
            foreach (Dictionary<string, object> game in ((List<object>)status["Games"]).Cast<Dictionary<string, object>>())
            {
                if ((string)game["Mode"] == "virtual" || (int)game["Relations"] > 0)
                    sb.AppendLine("  " + game["Id"] + " | " + game["Mode"] + " | " + game["Relations"] + " relation(s)" +
                                  ((string)game["Mode"] == "virtual" ? " | exes: " + (((List<string>)game["Exes"]).Count == 0 ? "none found" : string.Join("; ", (List<string>)game["Exes"])) : string.Empty) +
                                  (((string)game["SuggestedMode"]).Length > 0 && (string)game["Mode"] == "native" ? " | one-device game: " + game["SuggestedMode"] + " suggested (" + game["DeviceLimitNote"] + ")" : string.Empty) +
                                  ((int)game["Functions"] > 0 ? " | " + game["Functions"] + " virtual function(s)" : string.Empty));
                foreach (string warning in (List<string>)game["LimitWarnings"])
                    sb.AppendLine("    ! " + warning);
            }
            foreach (Dictionary<string, object> function in ((List<Dictionary<string, object>>)status["Functions"]))
                sb.AppendLine("Function " + function["Id"] + " (" + function["Game"] + "): " + function["Icon"] + " " + function["TypeName"] + " \"" + ((VirtualFunction)function["Function"]).Name + "\"");
            foreach (Dictionary<string, object> xg in ((List<object>)status["XboxGames"]).Cast<Dictionary<string, object>>())
            {
                sb.AppendLine("Xbox pad for " + xg["Game"] + ": " + xg["Slots"] + " slot(s)");
                foreach (Dictionary<string, object> slot in ((List<object>)status["XboxMapping"]).Cast<Dictionary<string, object>>().Where(s => (string)s["Game"] == (string)xg["Game"]))
                    sb.AppendLine("  " + slot["Source"] + " " + slot["Input"] + " -> " + slot["TargetName"] + ((bool)slot["Pinned"] ? " (game layout: " + slot["Action"] + ")" : string.Empty) + ((bool)slot["New"] ? " (new)" : string.Empty));
                foreach (string unfit in (List<string>)xg["Unfit"])
                    sb.AppendLine("  does not fit: " + unfit);
                foreach (string note in (List<string>)xg["Notes"])
                    sb.AppendLine("  note: " + note);
            }
            List<object> mapping = (List<object>)status["Mapping"];
            sb.AppendLine("Mapping: " + mapping.Count + " slot(s)");
            foreach (Dictionary<string, object> slot in mapping.Cast<Dictionary<string, object>>())
                sb.AppendLine("  " + slot["Source"] + " " + slot["Input"] + " -> vJoy " + slot["Device"] + " " + slot["Target"] +
                              ((bool)slot["New"] ? " (new)" : string.Empty) + ((bool)slot["Used"] ? string.Empty : " (unused)") + ((bool)slot["Connected"] ? string.Empty : " (not connected)"));
            foreach (Dictionary<string, object> need in ((List<object>)status["Needs"]).Cast<Dictionary<string, object>>())
                sb.AppendLine("Device " + need["Id"] + ": " + ((bool)need["Sufficient"] ? "configuration fits" : "needs " + string.Join(", ", (List<string>)need["Missing"]) + " -> " + need["ConfigCommand"]));
            AppendList(sb, "Errors", (List<string>)status["Errors"]);
            AppendList(sb, "Warnings", (List<string>)status["Warnings"]);
            return sb.ToString();
        }

        private static void AppendList(StringBuilder sb, string title, List<string> values)
        {
            if (values == null || values.Count == 0)
                return;
            sb.AppendLine(title + ":");
            foreach (string value in values)
                sb.AppendLine("  - " + value);
        }

        // ---- mode / map / settings / exe

        // A game's mode (native, virtual, xbox) with the mapping table made for it, as engine vjoy mode does; for Lay out with
        // AI. Returns the problem or "".
        internal static string SetMode(string game, string mode, List<string> warnings)
        {
            string gameId = ResolveGame(game);
            if (gameId.Length == 0)
                return "Unknown game: " + game;
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "vjoy")
                mode = VJoySettings.ModeVJoy;
            if (mode != "native" && mode != VJoySettings.ModeVJoy && mode != VJoySettings.ModeXbox)
                return "The mode is native, virtual (vJoy) or xbox.";
            VJoySettings settings = LoadSettings();
            if (mode == "native")
                settings.Games.Remove(gameId);
            else
                settings.Games[gameId] = mode;
            VJoyPlan plan = BuildPlan(settings, ReadVJoyDevices(), false, false);
            List<XboxPlan> xbox = BuildXboxPlans(settings, false, false, warnings);
            warnings.AddRange(plan.Warnings);
            if (plan.Errors.Count > 0)
                return string.Join("; ", plan.Errors);
            settings.Slots = plan.Slots;
            ApplyXboxPlans(settings, xbox);
            SaveSettings(settings);
            return string.Empty;
        }

        private static int Mode(List<string> args, bool textOutput, out string payload)
        {
            if (args.Count < 2)
                throw new ArgumentException("engine vjoy mode <game> native|virtual|xbox");
            string gameId = ResolveGame(args[0]);
            if (gameId.Length == 0)
                throw new ArgumentException("Unknown game: " + args[0]);
            string mode = args[1].Trim().ToLowerInvariant();
            if (mode == "vjoy")
                mode = VJoySettings.ModeVJoy;
            if (mode != "native" && mode != VJoySettings.ModeVJoy && mode != VJoySettings.ModeXbox)
                throw new ArgumentException("The mode is native, virtual (vJoy) or xbox (virtual Xbox 360 pad).");
            args.RemoveRange(0, 2);
            bool dryRun = PopFlag(args, "--dry-run");
            RefuseLeftovers(args, "mode");
            VJoySettings settings = LoadSettings();
            if (mode == "native")
                settings.Games.Remove(gameId);
            else
                settings.Games[gameId] = mode;
            VJoyPlan plan = BuildPlan(settings, ReadVJoyDevices(), false, false);
            List<string> warnings = new List<string>();
            List<XboxPlan> xbox = BuildXboxPlans(settings, false, false, warnings);
            plan.Warnings.AddRange(warnings);
            if (plan.Errors.Count == 0)
            {
                settings.Slots = plan.Slots;
                ApplyXboxPlans(settings, xbox);
            }
            if (!dryRun)
                SaveSettings(settings);
            string target = mode == VJoySettings.ModeVJoy ? "the virtual joystick (vJoy)" : mode == VJoySettings.ModeXbox ? "the virtual Xbox 360 pad (ViGEmBus)" : "the physical devices";
            IGameDeviceLimit limit = DeviceLimit(gameId);
            if (limit != null && !string.Equals(mode, limit.SuggestedMode, StringComparison.OrdinalIgnoreCase))
                plan.Warnings.Add(GameName(gameId) + " is a one-device game: " + limit.DeviceLimitNote + " Suggested: engine vjoy mode " + gameId + " " + limit.SuggestedMode + ".");
            payload = PlanPayload(plan, xbox, settings, textOutput, (dryRun ? "Dry run: " : string.Empty) + GameName(gameId) + " now uses " + target + ".");
            return plan.Errors.Count == 0 ? 0 : 1;
        }

        private static int MapCommand(List<string> args, bool planOnly, bool textOutput, out string payload)
        {
            bool reset = PopFlag(args, "--reset");
            bool prune = PopFlag(args, "--prune");
            bool dryRun = PopFlag(args, "--dry-run") || planOnly;
            RefuseLeftovers(args, planOnly ? "plan" : "map");
            VJoySettings settings = LoadSettings();
            VJoyPlan plan = BuildPlan(settings, ReadVJoyDevices(), reset, prune);
            List<string> warnings = new List<string>();
            List<XboxPlan> xbox = BuildXboxPlans(settings, reset, prune, warnings);
            plan.Warnings.AddRange(warnings);
            bool changed = plan.Changed || xbox.Any(x => x.Changed);
            if (!dryRun && plan.Errors.Count == 0)
            {
                settings.Slots = plan.Slots;
                ApplyXboxPlans(settings, xbox);
                SaveSettings(settings);
            }
            string headline = dryRun
                ? (planOnly ? "Plan (nothing saved)." : "Dry run: the mapping was not saved.")
                : (changed ? "Mapping saved in the profile." : "The mapping is up to date.");
            payload = PlanPayload(plan, xbox, settings, textOutput, headline);
            return plan.Errors.Count == 0 ? 0 : 1;
        }

        private static string PlanPayload(VJoyPlan plan, List<XboxPlan> xbox, VJoySettings settings, bool textOutput, string headline)
        {
            if (!textOutput)
            {
                return EngineSerialization.ToJson(new
                {
                    Message = headline,
                    VirtualGames = settings.VirtualGames(),
                    XboxGames = settings.XboxGames(),
                    Slots = plan.Slots,
                    Added = plan.Added,
                    Removed = plan.Removed,
                    Unused = plan.Unused,
                    Devices = plan.Devices,
                    Xbox = xbox.Select(x => new { x.Game, x.Slots, x.Added, x.Removed, x.Unfit, x.Warnings }).ToList(),
                    plan.Errors,
                    plan.Warnings
                });
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(headline);
            sb.AppendLine("Virtual games: " + (settings.VirtualGames().Count == 0 ? "none" : string.Join(", ", settings.VirtualGames())));
            if (settings.XboxGames().Count > 0)
                sb.AppendLine("Xbox games: " + string.Join(", ", settings.XboxGames()));
            foreach (XboxPlan x in xbox)
            {
                sb.AppendLine("Xbox pad for " + x.Game + ": " + x.Slots.Count + " slot(s) (added " + x.Added.Count + ", removed " + x.Removed.Count + ")");
                HashSet<VJoySlot> xAdded = new HashSet<VJoySlot>(x.Added);
                foreach (VJoySlot slot in x.Slots)
                    sb.AppendLine("  " + slot.Source + " " + slot.Input + " -> Xbox " + slot.Target + " (" + XboxControls.Describe(slot.Target) + ")" + (xAdded.Contains(slot) ? " (new)" : string.Empty));
                foreach (string unfit in x.Unfit)
                    sb.AppendLine("  does not fit: " + unfit);
                foreach (string warning in x.Warnings)
                    sb.AppendLine("  note: " + warning);
            }
            sb.AppendLine("Slots: " + plan.Slots.Count + " (added " + plan.Added.Count + ", removed " + plan.Removed.Count + ", unused " + plan.Unused.Count + ")");
            HashSet<VJoySlot> added = new HashSet<VJoySlot>(plan.Added);
            foreach (VJoySlot slot in plan.Slots)
                sb.AppendLine("  " + slot.Source + " " + slot.Input + " -> vJoy " + slot.Device + " " + slot.Target + (added.Contains(slot) ? " (new)" : string.Empty));
            foreach (VJoySlot slot in plan.Removed)
                sb.AppendLine("  removed: " + slot.Source + " " + slot.Input + " (vJoy " + slot.Device + " " + slot.Target + ")");
            foreach (VJoyDeviceNeed need in plan.Devices)
            {
                sb.AppendLine("vJoy device " + need.Id + ": " + need.Slots + " slot(s)" + (need.FunctionOutputs > 0 ? " + " + need.FunctionOutputs + " function output(s)" : string.Empty) + ", needs " +
                              (need.Axes.Count == 0 ? "no axes" : string.Join(",", need.Axes.Select(VJoyConfigArgs.AxisName))) + ", " + need.Buttons + " buttons, " + need.Povs + " hats; has " + need.Current +
                              (need.Sufficient ? " -> fits" : " -> configure: vJoyConfig " + Elevation.Quote(need.ConfigArguments)));
            }
            AppendList(sb, "Errors", plan.Errors);
            AppendList(sb, "Warnings", plan.Warnings);
            return sb.ToString();
        }

        private static int Settings(List<string> args, bool textOutput, out string payload)
        {
            VJoySettings settings = LoadSettings();
            string first = PopValue(args, "--first-device");
            string last = PopValue(args, "--last-device");
            bool? auto = ParseOnOff(PopValue(args, "--auto-start"), "--auto-start");
            bool? hide = ParseOnOff(PopValue(args, "--hide-devices"), "--hide-devices");
            bool dryRun = PopFlag(args, "--dry-run");
            RefuseLeftovers(args, "settings");
            int number;
            if (first != null)
            {
                if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1 || number > VJoySettings.MaxDevices)
                    throw new ArgumentException("--first-device takes 1..16.");
                settings.FirstDevice = number;
            }
            if (last != null)
            {
                if (!int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1 || number > VJoySettings.MaxDevices)
                    throw new ArgumentException("--last-device takes 1..16.");
                settings.LastDevice = number;
            }
            if (settings.LastDevice < settings.FirstDevice)
                throw new ArgumentException("--last-device must not be below --first-device.");
            if (auto.HasValue)
                settings.AutoStart = auto.Value;
            if (hide.HasValue)
                settings.HideDevices = hide.Value;
            settings.Normalize();
            if (!dryRun)
                SaveSettings(settings);
            string message = (dryRun ? "Dry run: " : string.Empty) + "vJoy devices " + settings.FirstDevice + "-" + settings.LastDevice + ", auto start " + (settings.AutoStart ? "on" : "off") + ", hide physical devices " + (settings.HideDevices ? "on" : "off") + ".";
            payload = textOutput ? message : EngineSerialization.ToJson(new { Message = message, settings.FirstDevice, settings.LastDevice, settings.AutoStart, settings.HideDevices });
            return 0;
        }

        private static int Exe(List<string> args, bool textOutput, out string payload)
        {
            if (args.Count < 1)
                throw new ArgumentException("engine vjoy exe <game> [--add <path>] [--remove <path>]");
            string gameId = ResolveGame(args[0]);
            if (gameId.Length == 0)
                throw new ArgumentException("Unknown game: " + args[0]);
            args.RemoveAt(0);
            List<string> add = PopValues(args, "--add");
            List<string> remove = PopValues(args, "--remove");
            RefuseLeftovers(args, "exe");
            Dictionary<string, List<string>> extra = LoadExtraExes();
            List<string> list;
            if (!extra.TryGetValue(gameId, out list))
                list = new List<string>();
            foreach (string path in add)
            {
                if (!File.Exists(path))
                    throw new ArgumentException("Executable not found: " + path);
                string full = Path.GetFullPath(path);
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    list.Add(full);
            }
            foreach (string path in remove)
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || string.Equals(p, SafeFull(path), StringComparison.OrdinalIgnoreCase));
            if (add.Count > 0 || remove.Count > 0)
            {
                if (list.Count == 0)
                    extra.Remove(gameId);
                else
                    extra[gameId] = list;
                SaveExtraExes(extra);
            }
            List<string> warnings = new List<string>();
            List<string> exes = GameExes(gameId, warnings);
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Game = gameId, Exes = exes, Extra = list, Warnings = warnings });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(GameName(gameId) + " executables HidHide blocks in virtual mode: " + exes.Count);
            foreach (string exe in exes)
                sb.AppendLine("  " + exe + (list.Contains(exe, StringComparer.OrdinalIgnoreCase) ? " (set by you)" : string.Empty));
            AppendList(sb, "Warnings", warnings);
            payload = sb.ToString();
            return 0;
        }

        private static string SafeFull(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return path;
            }
        }

        // ---- forwarder config

        // The table for JoyPro.VJoy.exe: sources by DirectInput instance GUID, slots as numbers.
        private static int ForwarderConfig(List<string> args, bool textOutput, out string payload)
        {
            string outPath = PopValue(args, "--out");
            string xboxGameRaw = PopValue(args, "--xbox-game");
            string functionGameRaw = PopValue(args, "--game");
            RefuseLeftovers(args, "forwarder-config");
            VJoySettings settings = LoadSettings();
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            VJoyPlan plan = BuildPlan(settings, caps, false, false);
            if (plan.Errors.Count > 0)
            {
                payload = string.Join(Environment.NewLine, plan.Errors);
                return 1;
            }
            // One virtual Xbox pad: the table of one Xbox game (the one asked for, else the only one, else the first).
            List<string> xboxGames = settings.XboxGames();
            string xboxGame = string.Empty;
            if (!string.IsNullOrWhiteSpace(xboxGameRaw))
            {
                xboxGame = ResolveGame(xboxGameRaw);
                if (!settings.IsXbox(xboxGame))
                {
                    payload = xboxGameRaw + " does not use the virtual Xbox pad (engine vjoy mode <game> xbox).";
                    return 1;
                }
            }
            else if (xboxGames.Count > 0)
                xboxGame = xboxGames[0];
            List<VJoySlot> xboxSlots = new List<VJoySlot>();
            if (xboxGame.Length > 0)
            {
                XboxPlan xp = BuildXboxPlans(settings, false, false, new List<string>()).First(x => string.Equals(x.Game, xboxGame, StringComparison.OrdinalIgnoreCase));
                xboxSlots = xp.Slots;
            }
            // Only the slots a vJoy game uses go to the forwarder (unused ones keep their place in the table).
            List<VJoySlot> vjoySlots = plan.Slots.Where(s => !plan.Unused.Contains(s)).ToList();
            // The virtual functions: of the game that started (--game), else of every vJoy game and the Xbox game.
            string functionGame = string.Empty;
            if (!string.IsNullOrWhiteSpace(functionGameRaw))
            {
                functionGame = ResolveGame(functionGameRaw);
                if (functionGame.Length == 0)
                {
                    payload = "Unknown game: " + functionGameRaw;
                    return 1;
                }
            }
            List<string> functionWarnings = new List<string>();
            List<KeyValuePair<string, VirtualFunction>> functions = settings.AllFunctions()
                .Where(f => f.Value.Enabled && (functionGame.Length > 0 ? string.Equals(f.Key, functionGame, StringComparison.OrdinalIgnoreCase) : settings.IsVirtual(f.Key) || string.Equals(f.Key, xboxGame, StringComparison.OrdinalIgnoreCase)))
                .Where(f => settings.IsVirtual(f.Key) || (string.Equals(f.Key, xboxGame, StringComparison.OrdinalIgnoreCase) && settings.IsXbox(f.Key)))
                .Select(f => new KeyValuePair<string, VirtualFunction>(f.Key, f.Value.Clone()))
                .ToList();
            List<string> sources = vjoySlots.Select(s => s.Source).Concat(xboxSlots.Select(s => s.Source))
                .Concat(functions.SelectMany(f => f.Value.Inputs).Where(i => i.Device.Length > 0 && i.Input.Length > 0).Select(i => i.Device))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            List<int> vjoyDeviceIds = vjoySlots.Select(s => s.Device)
                .Concat(functions.Where(f => settings.IsVirtual(f.Key)).SelectMany(f => VfResolver.VJoyTargets(f.Value)).Select(t => t.Key))
                .Distinct().OrderBy(d => d).ToList();
            List<VirtualFunction> resolved = new List<VirtualFunction>();
            foreach (KeyValuePair<string, VirtualFunction> entry in functions)
            {
                VirtualFunction function = entry.Value;
                FillShiftBase(function, settings.IsXbox(entry.Key) ? xboxSlots : vjoySlots, settings.IsXbox(entry.Key));
                List<string> problems = VirtualFunctionCatalog.Validate(function).Where(p => !p.EndsWith("no input chosen.", StringComparison.Ordinal)).ToList();
                problems.AddRange(VfResolver.Resolve(function, sources, vjoyDeviceIds));
                functionWarnings.AddRange(problems.Select(p => VJoyPlanner.VJoyFunctionLabel(entry.Key, function, null) + ": " + p));
                resolved.Add(function);
            }
            List<object> sourceItems = new List<object>();
            foreach (string source in sources)
            {
                string name;
                Guid guid;
                GameDeviceDirectory.TryParseJoyProName(source, out name, out guid);
                sourceItems.Add(new Dictionary<string, object> { { "device", source }, { "name", name }, { "instanceGuid", guid.ToString("D") } });
            }
            List<object> slots = new List<object>();
            foreach (VJoySlot slot in vjoySlots)
            {
                VJoyInput input;
                VJoyInput target;
                if (!VJoyInput.TryParseSlotToken(slot.Input, out input) || !VJoyInput.TryParseSlotToken(slot.Target, out target))
                    continue;
                slots.Add(new Dictionary<string, object>
                {
                    { "source", sources.FindIndex(s => string.Equals(s, slot.Source, StringComparison.OrdinalIgnoreCase)) },
                    { "kind", VJoyPlanner.KindName(input.Kind) },
                    { "index", input.Index },
                    { "device", slot.Device },
                    { "target", target.Index }
                });
            }
            List<object> xbox = new List<object>();
            foreach (VJoySlot slot in xboxSlots)
            {
                VJoyInput input;
                if (!VJoyInput.TryParseSlotToken(slot.Input, out input))
                    continue;
                xbox.Add(new Dictionary<string, object>
                {
                    { "source", sources.FindIndex(s => string.Equals(s, slot.Source, StringComparison.OrdinalIgnoreCase)) },
                    { "kind", VJoyPlanner.KindName(input.Kind) },
                    { "index", input.Index },
                    { "target", slot.Target }
                });
            }
            Dictionary<string, object> config = new Dictionary<string, object>
            {
                { "version", 1 },
                { "vjoyDevices", vjoyDeviceIds },
                { "sources", sourceItems },
                { "slots", slots },
                { "virtualGames", settings.VirtualGames() },
                { "xboxGame", xboxGame },
                { "xbox", xbox },
                { "functions", resolved }
            };
            string json = EngineSerialization.ToJson(config);
            if (string.IsNullOrWhiteSpace(outPath))
            {
                payload = json;
                return 0;
            }
            string directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(outPath, json, new UTF8Encoding(false));
            string message = "Wrote the forwarder table (" + slots.Count + " vJoy slot(s), " + xbox.Count + " Xbox slot(s)" + (xboxGame.Length > 0 ? " for " + xboxGame : string.Empty) + ", " + resolved.Count + " virtual function(s), " + sources.Count + " device(s)): " + outPath;
            payload = textOutput ? message + string.Concat(functionWarnings.Select(w => Environment.NewLine + "  - " + w)) : EngineSerialization.ToJson(new { Message = message, Path = outPath, Slots = slots.Count, XboxSlots = xbox.Count, XboxGame = xboxGame, Functions = resolved.Count, Sources = sources.Count, Warnings = functionWarnings });
            return 0;
        }

        // ---- vJoyConfig

        private static int ConfigureVJoy(List<string> args, bool textOutput, out string payload)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            // --max: every device JoyPro uses (or --device N) gets vJoy's maximum: 8 axes, 128 buttons, 4 hats.
            bool maximum = PopFlag(args, "--max");
            string deviceRaw = PopValue(args, "--device");
            RefuseLeftovers(args, "configure-vjoy");
            List<string> warnings = new List<string>();
            VJoyInstallInfo install = ProbeVJoy();
            if (!install.Installed)
            {
                payload = "vJoy is not installed (https://github.com/jshafer817/vJoy or https://sourceforge.net/projects/vjoystick).";
                return 1;
            }
            VJoySettings settings = LoadSettings();
            Dictionary<int, VJoyDeviceCaps> before = ReadVJoyDevices();
            VJoyPlan plan = BuildPlan(settings, before, false, false);
            if (plan.Errors.Count > 0)
            {
                payload = string.Join(Environment.NewLine, plan.Errors);
                return 1;
            }
            List<int> ids;
            List<List<string>> calls;
            if (maximum)
            {
                int single;
                if (deviceRaw != null && (!int.TryParse(deviceRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out single) || single < 1 || single > VJoySettings.MaxDevices))
                    throw new ArgumentException("--device takes 1..16.");
                ids = deviceRaw != null
                    ? new List<int> { int.Parse(deviceRaw, CultureInfo.InvariantCulture) }
                    : plan.Devices.Select(d => d.Id).Concat(before.Values.Where(c => c.Configured && c.Id >= settings.FirstDevice && c.Id <= settings.LastDevice).Select(c => c.Id)).DefaultIfEmpty(settings.FirstDevice).Distinct().OrderBy(d => d).ToList();
                calls = new List<List<string>>();
                foreach (int id in ids.ToList())
                {
                    VJoyDeviceCaps current;
                    if (!before.TryGetValue(id, out current))
                        current = new VJoyDeviceCaps { Id = id };
                    VJoyDeviceNeed need = plan.Devices.FirstOrDefault(d => d.Id == id) ?? new VJoyDeviceNeed { Id = id };
                    VJoyDeviceCaps target = VJoyPlanner.Maximum(id, current, need.Axes, plan.AvoidAxes);
                    if (VJoyPlanner.IsAtLeast(current, target))
                    {
                        ids.Remove(id);
                        continue;
                    }
                    calls.Add(VJoyConfigArgs.Create(target));
                }
            }
            else
            {
                List<VJoyDeviceNeed> needing = plan.Devices.Where(d => !d.Sufficient).ToList();
                ids = needing.Select(d => d.Id).ToList();
                calls = needing.Select(d => d.ConfigArguments).ToList();
            }
            List<string> commands = calls.Select(c => "vJoyConfig " + Elevation.Quote(c)).ToList();
            const string restart = " Windows must restart before the new vJoy setup takes effect (vJoy asks for it); JoyPro never restarts Windows - restart when it suits you, then export the virtual-mode games again.";
            string message;
            int exit = 0;
            bool cancelled = false;
            if (calls.Count == 0)
                message = maximum ? "The vJoy devices already have the maximum (8 axes, 128 buttons, 4 hats); nothing to configure." : "The vJoy devices already offer what the mapping needs; nothing to configure.";
            else if (dryRun)
                message = "Dry run: vJoy was not changed. With one administrator prompt JoyPro would run: " + string.Join(" ; ", commands) + "." + restart;
            else
            {
                exit = RunVJoyConfig(install, calls, warnings);
                cancelled = exit == HidHideCli.Cancelled;
                Dictionary<int, VJoyDeviceCaps> after = ReadVJoyDevices();
                List<string> problems = new List<string>();
                for (int i = 0; i < ids.Count && !cancelled; i++)
                {
                    VJoyDeviceCaps now;
                    if (!after.TryGetValue(ids[i], out now))
                        now = new VJoyDeviceCaps { Id = ids[i] };
                    VJoyDeviceCaps wanted = CapsFromConfigArguments(calls[i]);
                    if (!VJoyPlanner.IsAtLeast(now, wanted))
                        problems.Add("vJoy device " + ids[i] + " reads as " + now.Describe() + " (wanted " + wanted.Describe() + ").");
                }
                warnings.AddRange(problems);
                message = cancelled
                    ? "The Windows administrator prompt was declined; vJoy was not changed."
                    : problems.Count == 0 ? "vJoy is configured (" + string.Join(" ; ", commands) + ")." + restart : "vJoyConfig ended with exit code " + exit + "." + restart;
                if (!cancelled && problems.Count > 0)
                    exit = exit == 0 ? 1 : exit;
            }
            payload = textOutput
                ? message + Environment.NewLine + string.Join(Environment.NewLine, warnings.Select(w => "  - " + w))
                : EngineSerialization.ToJson(new { Message = message, DryRun = dryRun, Maximum = maximum, Cancelled = cancelled, RestartNeeded = calls.Count > 0 && !cancelled, Commands = commands, Warnings = warnings });
            return cancelled ? 2 : exit;
        }

        // ---- HidHide

        private static int ApplyHidHide(List<string> args, bool textOutput, out string payload)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            string ownerRaw = PopValue(args, "--owner-pid");
            List<string> extraExes = PopValues(args, "--exe");
            List<string> trusted = PopValues(args, "--trusted");
            RefuseLeftovers(args, "apply-hidhide");
            List<string> warnings = new List<string>();
            VJoySettings settings = LoadSettings();
            List<string> games = settings.AllVirtualGames();
            if (games.Count == 0)
            {
                payload = "No game uses virtual mode (engine vjoy mode <game> virtual); HidHide was not changed.";
                return 1;
            }
            Dictionary<int, VJoyDeviceCaps> caps = ReadVJoyDevices();
            DeviceScan scan = ScanDevices(null, caps);
            warnings.AddRange(scan.Warnings);
            VJoyPlan plan = BuildPlan(settings, caps, false, false);

            // HID interface paths; the elevated step turns them into HidHide's instance paths (--dev-gaming).
            ElevatedHidHideRequest request = new ElevatedHidHideRequest { Op = "enable", Games = games, JournalPath = JournalPath() };
            List<XboxPlan> xboxPlans = BuildXboxPlans(settings, false, false, warnings);
            foreach (string source in plan.Slots.Select(s => s.Source).Concat(xboxPlans.SelectMany(x => x.Slots).Select(s => s.Source)).Concat(FunctionSources(settings)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                GameDevice device = scan.Find(source);
                if (device == null)
                {
                    warnings.Add("Not connected, not hidden: " + source);
                    continue;
                }
                scan.Directory.EnsureCapabilities(device, warnings);
                if (string.IsNullOrEmpty(device.HidPath))
                    warnings.Add("No HID path for " + source + "; it cannot be hidden.");
                else
                    request.HideHidPaths.Add(device.HidPath);
            }
            foreach (VJoyPresence vjoy in scan.VJoy)
            {
                if (!string.IsNullOrEmpty(vjoy.HidPath))
                    request.KeepVisibleHidPaths.Add(vjoy.HidPath);
            }
            foreach (string game in games)
                request.GameExes.AddRange(GameExes(game, warnings));
            foreach (string exe in extraExes)
            {
                if (File.Exists(exe))
                    request.GameExes.Add(Path.GetFullPath(exe));
                else
                    warnings.Add("Executable not found: " + exe);
            }
            string engineDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? string.Empty;
            string forwarder = Path.GetFullPath(Path.Combine(engineDir, "..", "vjoy", "JoyPro.VJoy.exe"));
            if (File.Exists(forwarder))
                request.TrustedExes.Add(forwarder);
            foreach (string exe in trusted)
            {
                if (File.Exists(exe))
                    request.TrustedExes.Add(Path.GetFullPath(exe));
            }
            int ownerPid;
            if (ownerRaw != null && int.TryParse(ownerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerPid) && ownerPid > 0)
            {
                request.OwnerPid = ownerPid;
                request.OwnerStartTicks = ProcessStartTicks(ownerPid);
            }

            HidHideResult result;
            HidHideRequest resolved = null;
            if (dryRun)
            {
                // Without administrator rights: HidHide's hidden-device list cannot be read and --dev-gaming is not
                // asked (it can hang); the session says which parts are missing.
                IHidHideCli cli = CreateHidHide(false);
                resolved = ResolveRequest(request, cli, warnings);
                result = new HidHideSession(cli, request.JournalPath, IsProcessAlive).Enable(resolved, true);
            }
            else
            {
                HidHideJournal journal = CreateSession(CreateHidHide(false)).LoadJournal();
                if (journal != null && CreateSession(CreateHidHide(false)).IsStale(journal))
                    result = new HidHideResult { Message = "A previous virtual joystick session did not end cleanly; restore it first (engine vjoy recover)." };
                else
                    result = RunElevatedHidHide(request, warnings);
            }
            payload = HidHidePayload(result, warnings, textOutput, resolved);
            return result.Cancelled ? 2 : result.Ok ? 0 : 1;
        }

        private static int RestoreHidHide(List<string> args, bool recover, bool textOutput, out string payload)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            bool force = PopFlag(args, "--force");
            RefuseLeftovers(args, recover ? "recover" : "restore-hidhide");
            List<string> warnings = new List<string>();
            HidHideSession local = CreateSession(CreateHidHide(false));
            HidHideJournal journal = local.LoadJournal();
            HidHideResult result;
            if (journal == null)
                result = new HidHideResult { Ok = true, DryRun = dryRun, SessionPhase = "none", Message = "No JoyPro HidHide session: nothing to restore." };
            else if (recover && !force && !local.IsStale(journal))
                result = local.Recover(dryRun, false);
            else if (dryRun)
            {
                // The restore plan needs the full configuration; without administrator rights it can only be listed
                // from the journal.
                result = new HidHideResult { Ok = true, DryRun = true, SessionPhase = journal.Phase, Strategy = journal.Strategy == HidHideStrategy.AllowList ? "allow-list" : "block-list" };
                result.Message = "Dry run: HidHide was not changed. The restore takes back: " + journal.AddedDevices.Count + " hidden device(s), " + journal.AddedApps.Count +
                                 " program(s); puts back " + journal.RemovedDevices.Count + " device(s), " + journal.RemovedApps.Count + " program(s)" +
                                 (journal.SetCloak ? "; switches cloaking off" : string.Empty) + (journal.SetInverse ? "; sets the inverse mode back" : string.Empty) + ".";
            }
            else
                result = RunElevatedHidHide(new ElevatedHidHideRequest { Op = recover ? "recover" : "disable", Force = force, JournalPath = JournalPath() }, warnings);
            payload = HidHidePayload(result, warnings, textOutput, null);
            return result.Cancelled ? 2 : result.Ok ? 0 : 1;
        }

        // ---- the elevated step

        // What the elevated step does, passed on its command line (not in a file another program could change).
        internal sealed class ElevatedHidHideRequest
        {
            public string Op { get; set; }
            public bool Force { get; set; }
            public string JournalPath { get; set; }
            public List<string> HideHidPaths { get; set; }
            public List<string> KeepVisibleHidPaths { get; set; }
            public List<string> GameExes { get; set; }
            public List<string> TrustedExes { get; set; }
            public List<string> Games { get; set; }
            public int OwnerPid { get; set; }
            public long OwnerStartTicks { get; set; }

            public ElevatedHidHideRequest()
            {
                Op = JournalPath = string.Empty;
                HideHidPaths = new List<string>();
                KeepVisibleHidPaths = new List<string>();
                GameExes = new List<string>();
                TrustedExes = new List<string>();
                Games = new List<string>();
            }
        }

        // HID interface paths -> HidHide's device instance paths (its own --dev-gaming list, else derived).
        private static HidHideRequest ResolveRequest(ElevatedHidHideRequest request, IHidHideCli cli, List<string> warnings)
        {
            Dictionary<string, string> gaming = ReadGamingInstancePaths(cli, warnings);
            Func<string, string> resolve = hidPath =>
            {
                string exact;
                return gaming.TryGetValue(hidPath.Trim(), out exact) ? exact : InstancePathFromInterface(hidPath);
            };
            HidHideRequest resolved = new HidHideRequest
            {
                Games = request.Games,
                GameExes = request.GameExes,
                TrustedExes = request.TrustedExes,
                OwnerPid = request.OwnerPid,
                OwnerStartTicks = request.OwnerStartTicks
            };
            resolved.HideDevices.AddRange(request.HideHidPaths.Select(resolve).Where(p => p.Length > 0));
            resolved.KeepVisible.AddRange(request.KeepVisibleHidPaths.Select(resolve).Where(p => p.Length > 0));
            return resolved;
        }

        // One administrator prompt for the whole step: the elevated engine reads HidHide completely (the hidden-device
        // list needs the rights), writes the journal, applies, reads back and reports through a result file. With
        // JOYPRO_VJOY_FAKE the step runs as a normal child process (no prompt), so the tests use the same path.
        private static HidHideResult RunElevatedHidHide(ElevatedHidHideRequest request, List<string> warnings)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(EngineSerialization.ToJson(request)));
            Directory.CreateDirectory(DataFolder());
            string resultPath = Path.Combine(DataFolder(), "elevated-" + Guid.NewGuid().ToString("N") + ".json");
            List<string> arguments = new List<string> { "vjoy", "elevated-hidhide", "--request-b64", b64, "--result", resultPath };
            int exit;
            if (FakeFolder() == null && IsElevated())
            {
                exit = RunElevatedStep(arguments.Skip(2).ToList());
            }
            else
            {
                string engine = Process.GetCurrentProcess().MainModule.FileName;
                if (FakeFolder() != null)
                {
                    ProcessStartInfo info = new ProcessStartInfo(engine, Elevation.Quote(arguments)) { UseShellExecute = false, CreateNoWindow = true };
                    using (Process process = Process.Start(info))
                    {
                        if (!process.WaitForExit(120000))
                        {
                            KillTree(process.Id);
                            exit = HidHideCli.TimedOut;
                        }
                        else
                            exit = process.ExitCode;
                    }
                }
                else
                {
                    exit = Elevation.Run(engine, arguments);
                }
            }

            HidHideResult result = null;
            try
            {
                if (File.Exists(resultPath))
                {
                    result = EngineSerialization.FromJson<HidHideResult>(File.ReadAllText(resultPath, Encoding.UTF8));
                    File.Delete(resultPath);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                warnings.Add("The result of the elevated step could not be read: " + ex.Message);
            }
            if (result != null)
                return result;
            if (exit == HidHideCli.Cancelled)
                return new HidHideResult { Cancelled = true, Message = "The Windows administrator prompt was declined; HidHide was not changed." };
            return new HidHideResult { Message = "The elevated HidHide step did not report back (exit code " + exit.ToString(CultureInfo.InvariantCulture) + "); run engine vjoy status to see HidHide's state." };
        }

        // engine vjoy elevated-hidhide --request-b64 <base64 json> --result <file>: the elevated step (started by
        // apply-hidhide / restore-hidhide / recover with runas). Writes a HidHideResult to the result file.
        private static int RunElevatedStep(List<string> args)
        {
            string b64 = PopValue(args, "--request-b64");
            string resultPath = PopValue(args, "--result");
            if (string.IsNullOrWhiteSpace(b64) || string.IsNullOrWhiteSpace(resultPath))
                return 3;
            HidHideResult result;
            List<string> warnings = new List<string>();
            try
            {
                ElevatedHidHideRequest request = EngineSerialization.FromJson<ElevatedHidHideRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                if (FakeFolder() == null && !IsElevated())
                {
                    result = new HidHideResult { Message = "The HidHide step needs administrator rights." };
                }
                else
                {
                    IHidHideCli cli = CreateHidHide(true);
                    HidHideSession session = new HidHideSession(cli, string.IsNullOrWhiteSpace(request.JournalPath) ? JournalPath() : request.JournalPath, IsProcessAlive);
                    switch (request.Op)
                    {
                        case "enable":
                            result = session.Enable(ResolveRequest(request, cli, warnings), false);
                            break;
                        case "disable":
                            result = session.Disable(false);
                            break;
                        case "recover":
                            result = session.Recover(false, request.Force);
                            break;
                        default:
                            result = new HidHideResult { Message = "Unknown HidHide step: " + request.Op };
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result = new HidHideResult { Message = "The elevated HidHide step failed: " + ex.Message };
            }
            result.Notes.AddRange(warnings);
            try
            {
                File.WriteAllText(resultPath, EngineSerialization.ToJson(result), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 4;
            }
            return 0;
        }

        public static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        private static string HidHidePayload(HidHideResult result, List<string> warnings, bool textOutput, HidHideRequest request)
        {
            if (!textOutput)
            {
                return EngineSerialization.ToJson(new
                {
                    result.Ok,
                    result.DryRun,
                    result.Changed,
                    result.Cancelled,
                    result.Message,
                    result.Strategy,
                    result.Commands,
                    result.Notes,
                    result.Problems,
                    result.SessionPhase,
                    HideDevices = request == null ? new List<string>() : request.HideDevices,
                    GameExes = request == null ? new List<string>() : request.GameExes,
                    Warnings = warnings
                });
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(result.Message);
            if (!string.IsNullOrEmpty(result.Strategy))
                sb.AppendLine("Strategy: " + result.Strategy);
            sb.AppendLine("Session: " + result.SessionPhase);
            AppendList(sb, "HidHide commands" + (result.DryRun ? " (not run)" : string.Empty), result.Commands);
            AppendList(sb, "Notes", result.Notes);
            AppendList(sb, "Problems", result.Problems);
            AppendList(sb, "Warnings", warnings);
            return sb.ToString();
        }
    }

    // The displays' refresh rates (EnumDisplaySettings of every attached display); the forwarder polls at least this
    // often.
    internal static class DisplayRefresh
    {
        private const int EnumCurrentSettings = -1;
        private const int AttachedToDesktop = 0x1;

        public static int HighestRateHz()
        {
            int best = 0;
            try
            {
                DisplayDevice device = new DisplayDevice();
                device.cb = Marshal.SizeOf(typeof(DisplayDevice));
                for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
                {
                    if ((device.StateFlags & AttachedToDesktop) != 0)
                    {
                        DevMode mode = new DevMode();
                        mode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
                        if (EnumDisplaySettings(device.DeviceName, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency > 1)
                            best = Math.Max(best, mode.dmDisplayFrequency);
                    }
                    device = new DisplayDevice();
                    device.cb = Marshal.SizeOf(typeof(DisplayDevice));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
            }
            return Math.Max(60, best);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
    }
}
