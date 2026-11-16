using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // The Setup wizard's engine side (docs/setup-wizard.md): the connected devices with their proposed roles, the
    // aircraft of the chosen games, the plan (engine\SetupWizard, shared with the GUI) and applying it to the profile.
    internal static class EngineSetupWizard
    {
        internal sealed class DeviceChoices
        {
            public Dictionary<string, string> Grips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Added = new List<string>();
        }

        private static readonly Regex Diagonal = new Regex(@"\.(up|down)(left|right)$", RegexOptions.Compiled);

        // ---- devices ----

        public static List<SetupDevice> Devices(DeviceChoices choices, List<string> warnings, out List<SetupInput> inputs)
        {
            inputs = new List<SetupInput>();
            GameDeviceDirectory directory = GameDeviceDirectory.Discover(warnings);
            List<RosterDevice> roster = EngineDeviceKb.BuildRoster(directory, choices.Grips, choices.Added, null, warnings)
                .OrderBy(r => r.JoyProName, StringComparer.Ordinal).ToList();
            List<KbDevice> kb = EngineDeviceKb.Load(warnings);
            Dictionary<string, int> uses = ProductUses();

            List<SetupDevice> devices = new List<SetupDevice>();
            foreach (RosterDevice entry in roster)
            {
                GameDevice reported = directory.Devices.FirstOrDefault(d => d.JoyProName == entry.JoyProName);
                SetupDevice device = new SetupDevice
                {
                    Device = entry.JoyProName,
                    Connected = entry.Connected,
                    Generic = entry.Generic,
                    NeedsGrip = entry.Model == null && entry.GripChoices.Count > 0,
                    Model = entry.Model != null ? entry.Model.Name : DisplayName(entry.JoyProName),
                    ModelId = entry.Model != null && !entry.Generic ? entry.Model.Id : string.Empty,
                    Kind = entry.Model != null ? entry.Model.Kind : "base",
                    Hand = entry.Model != null ? entry.Model.Hand : HandFromName(entry.JoyProName),
                    VidPid = reported != null && reported.HasVidPid ? GameDeviceDirectory.VidPidKey(reported.VendorId, reported.ProductId).ToUpperInvariant() : string.Empty,
                    Buttons = entry.ButtonCount,
                    Hats = entry.PovCount,
                    Axes = entry.AxisSlots.Count,
                    ProfileUses = uses.ContainsKey(Product(entry.JoyProName)) ? uses[Product(entry.JoyProName)] : 0
                };
                if (reported != null && device.Buttons == 0 && device.Axes == 0)
                {
                    directory.EnsureCapabilities(reported, warnings);
                    device.Buttons = reported.ButtonCount;
                    device.Hats = reported.PovCount;
                    device.Axes = (reported.AxisSlots ?? new List<int>()).Count;
                }
                if (device.Hand == "either" && Regex.IsMatch(entry.JoyProName, @"^(l-|left\b)", RegexOptions.IgnoreCase))
                    device.Hand = "left";
                device.ForceFeedback = Regex.IsMatch(device.Model + " " + device.ModelId + " " + entry.JoyProName, @"ffb|rhino ffb|vpforce|brunner|force feedback|moza ab|\bab9\b|simucube", RegexOptions.IgnoreCase);
                foreach (KbDevice grip in entry.GripChoices)
                    device.Grips.Add(new SetupGripChoice { Id = grip.Id, Name = grip.Name, Image = grip.Image });
                string chosen;
                if (choices.Grips.TryGetValue(entry.JoyProName, out chosen) || choices.Grips.TryGetValue(Product(entry.JoyProName), out chosen))
                    device.Grip = chosen;
                devices.Add(device);
                inputs.AddRange(InputsOf(entry, device));
            }

            // Devices the catalog calls "other" (keyboards with joystick mode, vJoy, wheels, gamepads): listed, not bound.
            foreach (GameDevice reported in directory.Devices.Where(d => d.Connected && roster.All(r => r.JoyProName != d.JoyProName)).OrderBy(d => d.JoyProName, StringComparer.Ordinal))
            {
                string vidPid = reported.HasVidPid ? GameDeviceDirectory.VidPidKey(reported.VendorId, reported.ProductId).ToUpperInvariant() : string.Empty;
                KbDevice model = vidPid.Length > 0 ? kb.FirstOrDefault(k => k.VidPids.Contains(vidPid)) : null;
                string name = (reported.HidProductString + " " + reported.InstanceName + " " + (model != null ? model.Name : string.Empty)).ToLowerInvariant();
                string kind = Regex.IsMatch(name, @"vjoy|vigem|virtual") ? "virtual"
                    : Regex.IsMatch(name, @"wooting|keyboard") ? "keyboard"
                    : Regex.IsMatch(name, @"wheel|racing|moza r\d|\br\d+ base|fanatec|simucube|logitech g\d{2,3}\b") ? "wheel"
                    : Regex.IsMatch(name, @"gamepad|xbox|dualsense|dualshock|controller") ? "gamepad" : "other";
                devices.Add(new SetupDevice
                {
                    Device = reported.JoyProName,
                    Model = model != null ? model.Name : DisplayName(reported.JoyProName),
                    ModelId = model != null ? model.Id : string.Empty,
                    Kind = kind,
                    Connected = true,
                    VidPid = vidPid,
                    Buttons = reported.ButtonCount,
                    Hats = reported.PovCount,
                    Axes = (reported.AxisSlots ?? new List<int>()).Count
                });
            }

            foreach (SetupDevice device in devices)
            {
                string role;
                if (TryChoice(choices.Roles, device, out role))
                {
                    if (!SetupRoles.All.Contains(role))
                        warnings.Add("Unknown role '" + role + "' for " + device.Device + "; use " + string.Join(", ", SetupRoles.All) + ".");
                    else
                        device.Role = role;
                }
            }
            SetupRoles.Propose(devices);
            return devices;
        }

        private static bool TryChoice(Dictionary<string, string> choices, SetupDevice device, out string value)
        {
            if (choices.TryGetValue(device.Device, out value) || choices.TryGetValue(Product(device.Device), out value) ||
                (device.VidPid.Length > 0 && choices.TryGetValue(device.VidPid, out value)) || (device.Model.Length > 0 && choices.TryGetValue(device.Model, out value)))
                return true;
            value = null;
            return false;
        }

        // The inputs of a device: its catalog layout (base and grip), plus what Windows reports beyond it.
        private static IEnumerable<SetupInput> InputsOf(RosterDevice entry, SetupDevice device)
        {
            List<SetupInput> list = new List<SetupInput>();
            IEnumerable<KbControl> axes = (entry.BaseModel != null ? entry.BaseModel.Axes : Enumerable.Empty<KbControl>()).Concat(entry.Model != null ? entry.Model.Axes : Enumerable.Empty<KbControl>());
            IEnumerable<KbControl> controls = (entry.BaseModel != null ? entry.BaseModel.Controls : Enumerable.Empty<KbControl>()).Concat(entry.Model != null ? entry.Model.Controls : Enumerable.Empty<KbControl>());
            foreach (KbControl axis in axes.Where(a => a.Input.Length > 0))
            {
                if (list.Any(i => i.Input == axis.Input))
                    continue;
                list.Add(new SetupInput { Device = entry.JoyProName, Input = axis.Input, Control = axis.Control, Label = axis.Label ?? string.Empty, IsAxis = true, Type = SetupPlanner.AxisType(axis.Type), Hotas = axis.Hotas.ToList() });
            }
            foreach (KbControl control in controls.Where(c => c.Input.Length > 0 && !Diagonal.IsMatch(c.Control)))
            {
                if (list.Any(i => i.Input == control.Input))
                    continue;
                list.Add(new SetupInput { Device = entry.JoyProName, Input = control.Input, Control = control.Control, Label = control.Label ?? string.Empty, Type = control.Type, IsAxis = false, Hotas = control.Hotas.ToList() });
            }
            if (AssumeNumbers(entry, list, axes.ToList(), controls.ToList()))
                device.Assumed = true;
            string kind = entry.Model != null ? entry.Model.Kind : "stick";
            string family = kind == "stick" || kind == "throttle" || kind == "collective" || kind == "yoke" || kind == "base" ? (kind == "base" ? "stick" : kind)
                : kind == "hotas" || kind == "gamepad" ? "stick" : kind == "pedals" ? "pedals" : "panel";
            bool grip = family != "panel" && family != "pedals";
            if (family == "pedals")
                return list;   // pedal buttons are calibration / mode buttons
            int n = 0;
            foreach (int axis in entry.AxisSlots.Where(a => a >= 0 && a <= 7))
            {
                string token = GameInputTokens.AxisToken(axis);
                if (list.Any(i => string.Equals(i.Input, token, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = family + ".axis.extra" + (++n).ToString(CultureInfo.InvariantCulture), IsAxis = true, Type = "analog" });
            }
            for (int b = 0; b < entry.ButtonCount; b++)
            {
                string token = GameInputTokens.ButtonToken(b);
                if (list.Any(i => string.Equals(i.Input, token, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string number = (b + 1).ToString(CultureInfo.InvariantCulture);
                list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = grip ? family + ".base.button.in" + number : "panel.button.in" + number });
            }
            string[] dirs = { "up", null, "right", null, "down", null, "left", null };
            for (int h = 0; h < entry.PovCount; h++)
            {
                string hat = (h + 1).ToString(CultureInfo.InvariantCulture);
                for (int d = 0; d < 8; d += 2)
                {
                    string token = GameInputTokens.PovToken(h, d);
                    if (list.Any(i => string.Equals(i.Input, token, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = grip ? family + ".hat.pov" + hat + "." + dirs[d] : "panel.button.pov" + hat + dirs[d] });
                }
            }
            OneModePerThumbStick(list);
            return list;
        }

        private static readonly Regex ThumbDirection = new Regex(@"^(\w+)\.ministick(2?)\.(up|down|left|right|upright|downright|downleft|upleft)$", RegexOptions.Compiled);

        // A thumb stick that reports both a hat / direction mode and an analog mode (VKB A1 and Master Mode ministick, WinWing
        // analog sticks) is ONE physical control (review 5 M1, harness L19): the plan uses one mode, never trim on the hat and
        // the look axes on the analog mode. A hat mode reported as a POV is the device's default (VKB: the A1 click switches
        // to analog), so the axes go; direction buttons beside the axes are virtual buttons of the analog stick, so they go.
        internal static void OneModePerThumbStick(List<SetupInput> list)
        {
            foreach (IGrouping<string, SetupInput> stick in list.Where(i => !i.IsAxis && ThumbDirection.IsMatch(i.Control ?? string.Empty))
                         .GroupBy(i => i.Device + "|" + ThumbDirection.Match(i.Control).Groups[1].Value + "|" + ThumbDirection.Match(i.Control).Groups[2].Value).ToList())
            {
                string[] key = stick.Key.Split('|');
                Regex axisOf = new Regex("^" + Regex.Escape(key[1]) + @"\.axis\.ministick" + key[2] + @"\.[xy]$");
                List<SetupInput> axes = list.Where(i => i.IsAxis && i.Device == key[0] && axisOf.IsMatch(i.Control ?? string.Empty)).ToList();
                if (axes.Count == 0)
                    continue;
                bool povMode = stick.Any(i => (i.Input ?? string.Empty).StartsWith("JOY_BTN_POV", StringComparison.OrdinalIgnoreCase));
                foreach (SetupInput gone in povMode ? axes : stick.ToList())
                    list.Remove(gone);
            }
        }

        private static readonly string[] AxisOrder = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2" };
        private static readonly Regex HatDirection = new Regex(@"^(.*\.hat\.[^.]+)\.(up|down|left|right)$", RegexOptions.Compiled);

        // A catalog model whose DirectInput numbers are not published (low-confidence grips, many devices added from the
        // catalog): the usual numbering instead of none, so the plan still knows the trigger, the hats and the axes -
        // button 1 = the trigger, hat 1 = the first hat, stick X / Y = roll / pitch, twist = RZ, pedals X / Y = toe brakes,
        // RZ = rudder, a throttle's main lever Z - then the catalog's order. A connected device only gets numbers it
        // reports. The wizard says so (SetupRoles.Rules) and the review shows every input.
        private static bool AssumeNumbers(RosterDevice entry, List<SetupInput> list, List<KbControl> axes, List<KbControl> controls)
        {
            bool connected = entry.Connected;
            HashSet<string> used = new HashSet<string>(list.Select(i => i.Input), StringComparer.OrdinalIgnoreCase);
            bool assumed = false;
            // Partly numbered axes (pedals whose toe brakes are numbered but not the rudder: WinWing Orion 32 / Cyber Taurus;
            // a stick whose twist and slider are not: Fox 2 Pro Shock): the unnumbered ones take the usual places too (rudder
            // and twist RZ, else the first free axis the device reports).
            if (axes.Count > 0 && axes.Any(a => string.IsNullOrEmpty(a.Input) && !string.IsNullOrEmpty(a.Control)))
            {
                foreach (KbControl axis in axes.Where(a => !string.IsNullOrEmpty(a.Control) && string.IsNullOrEmpty(a.Input)))
                {
                    string c = axis.Control;
                    string wanted = Regex.IsMatch(c, @"\.axis\.roll$") ? "JOY_X" : Regex.IsMatch(c, @"\.axis\.pitch$") ? "JOY_Y" : Regex.IsMatch(c, @"\.axis\.(twist|rudder)$") ? "JOY_RZ"
                        : c.EndsWith(".axis.brake_left", StringComparison.Ordinal) ? "JOY_X" : c.EndsWith(".axis.brake_right", StringComparison.Ordinal) ? "JOY_Y"
                        : Regex.IsMatch(c, @"^(throttle|quadrant|collective)\.axis\.main$") ? "JOY_Z" : null;
                    Func<string, bool> fits = t => t != null && !used.Contains(t) &&
                                                       (!connected || entry.AxisSlots.Any(s => string.Equals(GameInputTokens.AxisToken(s), t, StringComparison.OrdinalIgnoreCase)));
                    string token = fits(wanted) ? wanted : AxisOrder.FirstOrDefault(fits);
                    if (token == null)
                        continue;
                    used.Add(token);
                    list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = c, Label = axis.Label ?? string.Empty, IsAxis = true, Type = SetupPlanner.AxisType(axis.Type) });
                    assumed = true;
                }
            }
            List<KbControl> open = controls.Where(c => !string.IsNullOrEmpty(c.Control) && !Diagonal.IsMatch(c.Control)).ToList();
            if (open.Count == 0 || controls.Any(c => !string.IsNullOrEmpty(c.Input)))
                return assumed;
            // Hats: each 4-way hat of the catalog on the next free POV.
            HashSet<KbControl> done = new HashSet<KbControl>();
            int pov = 0;
            foreach (IGrouping<string, KbControl> hat in open.Where(c => HatDirection.IsMatch(c.Control)).GroupBy(c => HatDirection.Match(c.Control).Groups[1].Value, StringComparer.Ordinal))
            {
                List<KbControl> directions = hat.GroupBy(c => HatDirection.Match(c.Control).Groups[2].Value, StringComparer.Ordinal).Select(g => g.First()).ToList();
                if (directions.Count < 2)
                    continue;
                while (pov < 4 && used.Any(u => u.StartsWith("JOY_BTN_POV" + (pov + 1).ToString(CultureInfo.InvariantCulture) + "_", StringComparison.OrdinalIgnoreCase)))
                    pov++;
                if (pov >= 4 || (connected && pov >= entry.PovCount))
                    break;
                foreach (KbControl direction in directions)
                {
                    string dir = HatDirection.Match(direction.Control).Groups[2].Value;
                    int d = dir == "up" ? 0 : dir == "right" ? 2 : dir == "down" ? 4 : 6;
                    string token = GameInputTokens.PovToken(pov, d);
                    used.Add(token);
                    list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = direction.Control, Label = direction.Label ?? string.Empty, Type = direction.Type, IsAxis = false });
                    done.Add(direction);
                }
                pov++;
                assumed = true;
            }
            // Buttons: the trigger is button 1, then the catalog's order.
            int limit = connected ? entry.ButtonCount : 128;
            int next = 0;
            foreach (KbControl control in open.Where(c => !done.Contains(c)).OrderBy(c => c.Control == "stick.trigger.stage1" ? 0 : 1))
            {
                while (next < limit && used.Contains(GameInputTokens.ButtonToken(next)))
                    next++;
                if (next >= limit)
                    break;
                string token = GameInputTokens.ButtonToken(next);
                used.Add(token);
                list.Add(new SetupInput { Device = entry.JoyProName, Input = token, Control = control.Control, Label = control.Label ?? string.Empty, Type = control.Type, IsAxis = false });
                assumed = true;
            }
            return assumed;
        }

        // Binds of the profile per product ("vkb-sim gunfighter modern combat pro twist"): the user's main devices.
        private static Dictionary<string, int> ProductUses()
        {
            Dictionary<string, int> uses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Bind bind in InternalDataManagement.AllBinds.Values.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Joystick)))
            {
                string product = Product(bind.Joystick);
                uses[product] = (uses.ContainsKey(product) ? uses[product] : 0) + 1;
            }
            return uses;
        }

        private static string DisplayName(string joyProName)
        {
            return Regex.Replace(Regex.Replace(joyProName ?? string.Empty, @"\{[0-9A-Fa-f-]+\}\s*$", string.Empty), @"\s+", " ").Trim();
        }

        internal static string Product(string joyProName)
        {
            string name = Regex.Replace(joyProName ?? string.Empty, @"\{[0-9A-Fa-f-]+\}\s*$", string.Empty);
            return Regex.Replace(name, @"\s+", " ").Trim().ToLowerInvariant();
        }

        private static string HandFromName(string name)
        {
            string n = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (Regex.IsMatch(n, @"^(l-|l\s)|\bleft\b"))
                return "left";
            if (Regex.IsMatch(n, @"^(r-|r\s)|\bright\b"))
                return "right";
            return "either";
        }

        // ---- the request ----

        // The catalog part of a request (functions, aircraft, the commands the local databases know, the cockpit
        // database): the same for every setup of the same games. setup eval keeps it between plans (CacheCatalog).
        internal sealed class CatalogPart
        {
            public Dictionary<string, SetupFunction> Functions = new Dictionary<string, SetupFunction>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, SetupAircraft> Aircraft = new Dictionary<string, SetupAircraft>(StringComparer.OrdinalIgnoreCase);
            public List<SetupCommand> AllCommands = new List<SetupCommand>();
            public List<SetupCommand> Commands = new List<SetupCommand>();
            public List<string> Warnings = new List<string>();
            public List<string> Notes = new List<string>();
            public ICockpitPositions Cockpits;
            public bool Loaded;
        }

        // setup eval runs hundreds of plans of the same games: their catalog part is read once (off for the CLI and GUI,
        // which read it fresh every run).
        internal static bool CacheCatalog;
        private static readonly Dictionary<string, CatalogPart> catalogCache = new Dictionary<string, CatalogPart>(StringComparer.OrdinalIgnoreCase);

        internal static CatalogPart Catalog(SetupOptions options)
        {
            List<string> only = (options.OnlyAircraft ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
            string key = string.Join(",", (options.Games ?? new List<string>()).OrderBy(g => g, StringComparer.OrdinalIgnoreCase)) + "|" +
                         string.Join(",", only.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) + "|" + options.ExistingOnly;
            CatalogPart part;
            if (CacheCatalog && catalogCache.TryGetValue(key, out part))
                return part;
            part = LoadCatalog(options, only);
            if (CacheCatalog)
                catalogCache[key] = part;
            return part;
        }

        private static CatalogPart LoadCatalog(SetupOptions options, List<string> only)
        {
            CatalogPart part = new CatalogPart();
            Dictionary<string, SetupFunction> functions;
            Dictionary<string, SetupAircraft> aircraft;
            List<SetupCommand> commands;
            List<string> errors = new List<string>();
            if (!SetupCatalog.Load(FunctionsFolder(), errors, out functions, out aircraft, out commands))
            {
                part.Warnings.AddRange(errors);
                return part;
            }
            part.Warnings.AddRange(errors);
            part.Loaded = true;
            part.Functions = functions;
            part.Aircraft = aircraft;
            part.AllCommands = commands;

            // Only the DCS modules the user has (--only-aircraft): the input layers and the other games stay.
            HashSet<string> onlySet = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
            Func<string, string, bool> wanted = (game, plane) =>
            {
                if (onlySet.Count == 0 || !EngineAutoGroup.IsDcs(game) || onlySet.Contains(plane))
                    return true;
                SetupAircraft known;
                return aircraft.TryGetValue(plane, out known) && known.SharedLayer;
            };

            // Only commands the local database knows, with the type of their function (like Auto Group).
            HashSet<string> games = new HashSet<string>(options.Games, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> dbIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool?> axisCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (Lay out with AI with the profile's relations only needs none of them: no new relations are made.)
            foreach (SetupCommand command in options.ExistingOnly ? new List<SetupCommand>() : commands)
            {
                SetupFunction function;
                if (!games.Contains(command.Game) || !functions.TryGetValue(command.Function, out function) || !aircraft.ContainsKey(command.Aircraft) || !wanted(command.Game, command.Aircraft))
                    continue;
                // The same scope as the export: Star Citizen's export writes ship controls only (spaceship_*, the ship
                // maps), so the plan does not offer the head-tracking map's voice push-to-talk either.
                if (string.Equals(command.Game, "StarCitizen", StringComparison.OrdinalIgnoreCase) && command.Action.IndexOf("___", StringComparison.Ordinal) > 0 &&
                    !EngineStarCitizen.IsFlightMap(command.Action.Substring(0, command.Action.IndexOf("___", StringComparison.Ordinal))))
                    continue;
                bool? isAxis;
                if (EngineAutoGroup.IsDcs(command.Game))
                {
                    HashSet<string> known = EngineAutoGroup.DatabaseIds(dbIds, command.Aircraft);
                    if (known.Count == 0)
                    {
                        missing.Add(command.Aircraft);
                        continue;
                    }
                    isAxis = known.Contains(command.Action) ? command.Action.StartsWith("a", StringComparison.OrdinalIgnoreCase) : (bool?)null;
                }
                else
                {
                    string key = command.Game + "|" + command.Action;
                    if (!axisCache.TryGetValue(key, out isAxis))
                        axisCache[key] = isAxis = EngineAutoGroup.GameCommandAxis(command.Game, command.Action);
                }
                if (!isAxis.HasValue || (isAxis.Value != function.IsAxis && !KeyOnAxisCommand(command.Game, command.Action, function) && !EngineAutoGroup.PadButtonsOnAxis(command.Game, function.IsAxis, isAxis.Value)))
                    continue;
                part.Commands.Add(command);
            }
            if (!options.ExistingOnly)
            {
                SetupRequest scratch = new SetupRequest { Functions = functions, Aircraft = aircraft, Commands = part.Commands };
                AddMapCommands(scratch, games);
                part.Commands = scratch.Commands.Where(c => wanted(c.Game, c.Aircraft)).ToList();
                part.Warnings.AddRange(scratch.Warnings);
                part.Notes.AddRange(scratch.Notes);
            }

            // The cockpit database itself (docs/cockpits.md): every game aircraft a variant names.
            CockpitPositionTable cockpits = new CockpitPositionTable();
            cockpits.AddCatalog(Program.LoadCockpitCatalog(), "DB\\Cockpits");
            cockpits.AddEquivalenceFile(FunctionsFolder(), part.Warnings);
            part.Cockpits = cockpits;
            if (missing.Count > 0)
                part.Warnings.Add("Not in the local DCS database (refresh it to include them): " + string.Join(", ", missing.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).Take(12)) + (missing.Count > 12 ? " ..." : string.Empty));
            return part;
        }

        // Plain names (the title layer): a function whose commands of the plan all belong to one game other than DCS - every
        // relation of a one-game plan, the pad games (ACE COMBAT 8, HLL: Vietnam) - is named after the game's action ("Map",
        // "Radar map", "Change weapon", "Fire gun"), not after the catalog slot a novice cannot read ("Kneeboard TOGGLE",
        // "Select Heater", "Trigger1stStage"); several actions share their first words ("Throttle up" + "Throttle down" ->
        // "Throttle", "Yaw left" + "Yaw right" -> "Yaw"). A title two functions would share keeps the slot names (one
        // relation per function). Copies: the catalog stays as it is; Auto Group and suggestions find the function from the
        // relation's commands (EngineRelationPurpose), not from its name.
        internal static Dictionary<string, SetupFunction> PadFunctionNames(Dictionary<string, SetupFunction> functions, List<SetupCommand> commands)
        {
            List<IGrouping<string, SetupCommand>> byFunction = commands.Where(c => c != null && !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Function, StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, Dictionary<string, string>> titles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SetupFunction> renamed = null;
            Dictionary<string, string> proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, SetupCommand> group in byFunction)
            {
                SetupFunction function;
                if (!functions.TryGetValue(group.Key, out function) || group.Select(c => EngineAutoGroup.IsDcs(c.Game) ? "DCS" : c.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                    EngineAutoGroup.IsDcs(group.First().Game))
                    continue;
                List<string> names = new List<string>();
                foreach (SetupCommand command in group)
                {
                    Dictionary<string, string> game;
                    if (!titles.TryGetValue(command.Game, out game))
                    {
                        titles[command.Game] = game = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        IGameIntegration integration;
                        if (GameRegistry.TryResolve(command.Game, out integration))
                        {
                            foreach (GameAction action in GameCatalogStore.Load(integration, new List<string>()).Actions)
                                game[action.Id] = Regex.Replace(action.Title ?? string.Empty, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
                        }
                    }
                    string title;
                    game.TryGetValue(command.Action, out title);
                    // (JoyPro's own action names - DB\Functions\ActionTexts - where the game has a table: IL-2's "Map mode")
                    try
                    {
                        title = EngineActionTexts.Title(command.Game, command.Action, title ?? string.Empty) ?? string.Empty;
                        // (Star Citizen ids of the function tables are "actionmap___action", JoyPro's names "actionmap|action")
                        if (command.Action.IndexOf("___", StringComparison.Ordinal) > 0)
                            title = EngineActionTexts.Title(command.Game, command.Action.Replace("___", "|"), title) ?? title;
                    }
                    catch (Exception)
                    {
                        title = title ?? string.Empty;
                    }
                    title = Regex.Replace(title, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
                    if (title.Length > 0 && !names.Contains(title, StringComparer.OrdinalIgnoreCase))
                        names.Add(title);
                }
                string name = CommonWords(names);
                // (a title must read as a name: not a raw id - "v_weapon_preset_attack", "KEY_GEAR_UP" - and not empty)
                if (name.Length < 2 || string.Equals(name, function.Name, StringComparison.Ordinal) || Regex.IsMatch(name, @"^[A-Za-z0-9]+(_[A-Za-z0-9]+)+$"))
                    continue;
                proposed[group.Key] = name;
            }
            // One title, one function: titles two functions would share stay slot names.
            foreach (IGrouping<string, KeyValuePair<string, string>> same in proposed.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (same.Count() > 1 || functions.Values.Any(f => !proposed.ContainsKey(f.Id) && string.Equals(f.Name, same.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                KeyValuePair<string, string> only = same.First();
                if (renamed == null)
                    renamed = new Dictionary<string, SetupFunction>(functions, StringComparer.OrdinalIgnoreCase);
                SetupFunction copy = functions[only.Key].Copy();
                copy.Name = only.Value;
                renamed[only.Key] = copy;
            }
            return renamed ?? functions;
        }

        // An aircraft or game of the plan that flies (fixed wing, helicopter) but whose catalog rows have no pitch or roll
        // (a game table JoyPro has not filled yet): its quality must not read 100 %. The missing flight axes count as
        // essential functions not placed, and the plan says so.
        internal static readonly string[] FlightAxes = { "pitch", "roll" };

        internal static void FlightAxesGuard(SetupPlan plan, SetupRequest request)
        {
            foreach (SetupQuality quality in plan.Quality)
            {
                SetupAircraft plane;
                if (!request.Aircraft.TryGetValue(quality.Aircraft, out plane) || plane.SharedLayer || (plane.Type != "fixed_wing" && plane.Type != "rotary_wing"))
                    continue;
                List<string> missing = FlightAxes.Where(f => !request.Commands.Any(c => string.Equals(c.Aircraft, quality.Aircraft, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Function, f, StringComparison.OrdinalIgnoreCase)) &&
                                                             !request.Existing.Any(e => string.Equals(e.Function, f, StringComparison.OrdinalIgnoreCase) && e.Nodes.Any(n => string.Equals(n.Aircraft, quality.Aircraft, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                if (missing.Count == 0)
                    continue;
                quality.Score = Math.Round(quality.Score * quality.Total / (double)(quality.Total + missing.Count), 1);
                quality.Total += missing.Count;
                quality.EssentialTotal += missing.Count;
                plan.Warnings.Add(quality.Aircraft + ": JoyPro's function catalog has no " + string.Join(" / ", missing) + " for it yet, so the plan cannot put the flight controls on your devices. Set them in the game.");
            }
        }

        // The words all names start with ("Throttle up", "Throttle down" -> "Throttle"); one name: itself.
        private static string CommonWords(List<string> names)
        {
            if (names.Count == 0)
                return string.Empty;
            if (names.Count == 1)
                return names[0];
            string[] first = names[0].Split(' ');
            int count = 0;
            while (count < first.Length && names.All(n => { string[] w = n.Split(' '); return w.Length > count && string.Equals(w[count], first[count], StringComparison.OrdinalIgnoreCase); }))
                count++;
            return string.Join(" ", first.Take(count)).Trim(' ', ':', '-', '/');
        }

        public static SetupRequest Request(List<SetupDevice> devices, List<SetupInput> inputs, SetupOptions options, List<string> cockpitFiles, List<string> warnings)
        {
            SetupRequest request = new SetupRequest { Devices = devices, Inputs = inputs, Options = options };
            request.Warnings.AddRange(warnings);
            foreach (string game in options.Games ?? new List<string>())
                request.ModifierSupport[game] = EngineGameSupport.ModifierSupport(game);
            CatalogPart catalog = Catalog(options);
            request.Warnings.AddRange(catalog.Warnings.Where(w => !w.StartsWith("Not in the local DCS database", StringComparison.Ordinal)));
            if (!catalog.Loaded)
                return request;
            Dictionary<string, SetupFunction> functions = catalog.Functions;
            List<SetupCommand> commands = catalog.AllCommands;
            request.Aircraft = new Dictionary<string, SetupAircraft>(catalog.Aircraft, StringComparer.OrdinalIgnoreCase);
            request.Commands = new List<SetupCommand>(catalog.Commands);
            Program.CommandTitles(request.Commands, request.Warnings);
            request.Functions = functions = PadFunctionNames(functions, request.Commands);
            request.Notes.AddRange(catalog.Notes);

            // The profile: its relations, binds (mapped onto this setup by product) and modifiers.
            HashSet<string> binding = new HashSet<string>(devices.Where(d => SetupRoles.Binds(d.Role)).Select(d => d.Device), StringComparer.Ordinal);
            Dictionary<string, List<string>> slotsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupCommand command in commands.Where(c => !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
            {
                string key = SetupPlanner.CommandKey(EngineAutoGroup.IsDcs(command.Game) ? "DCS" : command.Game, command.Action, command.Aircraft);
                List<string> list;
                if (!slotsOf.TryGetValue(key, out list))
                    slotsOf[key] = list = new List<string>();
                list.Add(command.Function);
            }
            Dictionary<string, string> byName = functions.Values.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                    continue;
                SetupExistingRelation existing = new SetupExistingRelation { Name = pair.Key, IsAxis = relation.ISAXIS };
                Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in relation.NODES.Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                    List<string> planes = new List<string>();
                    if (EngineAutoGroup.IsDcs(game))
                    {
                        foreach (KeyValuePair<string, bool> plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()))
                        {
                            if (plane.Value)
                                planes.Add(plane.Key);
                            else
                                request.Disabled.Add(SetupPlanner.CommandKey("DCS", node.ID, plane.Key));
                        }
                        game = "DCS";
                    }
                    else
                    {
                        planes.Add(game);
                    }
                    foreach (string plane in planes)
                    {
                        existing.Nodes.Add(new SetupNode { Game = game, Action = node.ID, Aircraft = plane });
                        List<string> slots;
                        if (slotsOf.TryGetValue(SetupPlanner.CommandKey(game, node.ID, plane), out slots))
                        {
                            foreach (string slot in slots)
                                votes[slot] = (votes.ContainsKey(slot) ? votes[slot] : 0) + 1;
                        }
                    }
                }
                string named;
                existing.Function = byName.TryGetValue(Regex.Replace(pair.Key, @"\s*\[[^\]]*\]$", string.Empty), out named) ? named
                    : votes.Count > 0 ? votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key : string.Empty;
                Bind bind;
                InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind);
                string input = bind == null ? string.Empty : relation.ISAXIS ? bind.JAxis : bind.JButton;
                if (bind != null && !string.IsNullOrWhiteSpace(bind.Joystick) && !string.IsNullOrWhiteSpace(input))
                {
                    existing.Device = MapDevice(bind.Joystick, devices);
                    existing.Input = input.Trim();
                    Modifier modifier = bind.AllReformers != null && bind.AllReformers.Count > 0 ? Modifier.ReformerToMod(bind.AllReformers[0]) : null;
                    existing.Modifier = modifier == null ? string.Empty : modifier.name ?? string.Empty;
                    existing.OnSetup = binding.Contains(existing.Device);
                }
                request.Existing.Add(existing);
            }
            Dictionary<string, int> modifierUses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Bind bind in InternalDataManagement.AllBinds.Values.Where(b => b != null && b.AllReformers != null))
            {
                foreach (string reformer in bind.AllReformers)
                {
                    Modifier parsed = Modifier.ReformerToMod(reformer);
                    if (parsed != null)
                        modifierUses[parsed.name ?? string.Empty] = (modifierUses.ContainsKey(parsed.name ?? string.Empty) ? modifierUses[parsed.name ?? string.Empty] : 0) + 1;
                }
            }
            foreach (Modifier modifier in InternalDataManagement.AllModifiers.Values.Where(m => m != null && !string.IsNullOrWhiteSpace(m.device)))
            {
                request.ExistingModifiers.Add(new SetupModifierChoice
                {
                    Name = modifier.name ?? string.Empty,
                    Device = MapDevice(modifier.device, devices),
                    Input = modifier.key ?? string.Empty,
                    Uses = modifierUses.ContainsKey(modifier.name ?? string.Empty) ? modifierUses[modifier.name ?? string.Empty] : 0
                });
            }

            // The cockpit database: files the caller passes (engine cockpits get ... --json) first, then DB\Cockpits\*.json.
            if (cockpitFiles != null && cockpitFiles.Count > 0)
            {
                CockpitPositionTable cockpits = new CockpitPositionTable();
                foreach (string file in cockpitFiles)
                {
                    if (!File.Exists(file))
                    {
                        request.Warnings.Add("Cockpit entry not found: " + file);
                        continue;
                    }
                    cockpits.AddJson(File.ReadAllText(file, Encoding.UTF8), Path.GetFileNameWithoutExtension(file), request.Warnings);
                }
                // The cockpit database itself (docs/cockpits.md): every game aircraft a variant names, unless a file above
                // already gave it.
                cockpits.AddCatalog(Program.LoadCockpitCatalog(), "DB\\Cockpits");
                cockpits.AddEquivalenceFile(FunctionsFolder(), request.Warnings);
                request.Cockpits = cockpits;
            }
            else
            {
                request.Cockpits = catalog.Cockpits;
            }
            request.Warnings.AddRange(catalog.Warnings.Where(w => w.StartsWith("Not in the local DCS database", StringComparison.Ordinal)));
            // New or unused aircraft follow the layout of the user's most used aircraft of their class (usage ranking).
            Program.AddUsageStyleRules(request);
            return request;
        }

        // The function knowledge map (EngineFunctionMap, as Auto Group uses it): the actions, aircraft and games the
        // catalog rows do not list join the slot the catalog uses most for their function (War Thunder, MSFS, X-Plane ...).
        internal static void AddMapCommands(SetupRequest request, HashSet<string> games)
        {
            EngineAutoGroupResult scratch = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> slots;
            Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
            List<EngineAutoGroup.Row> rows;
            if (!EngineAutoGroup.TryLoadCatalog(FunctionsFolder(), scratch, out slots, out aircraft, out rows))
                return;
            List<EngineAutoGroup.Row> usable = request.Commands.Select(c => new EngineAutoGroup.Row { Game = c.Game, Plane = c.Aircraft, Slot = c.Function, Action = c.Action, Status = c.Status }).ToList();
            int before = usable.Count;
            try
            {
                EngineAutoGroup.AddMapRows(slots, aircraft, usable, scratch);
            }
            catch (Exception ex)
            {
                request.Warnings.Add("The function map could not be used: " + ex.Message);
                return;
            }
            int added = 0;
            foreach (EngineAutoGroup.Row row in usable.Skip(before))
            {
                string game = EngineAutoGroup.IsDcs(row.Game) ? "DCS" : row.Game;
                SetupFunction function;
                if (!games.Contains(game) || !request.Functions.TryGetValue(row.Slot, out function))
                    continue;
                if (!request.Aircraft.ContainsKey(row.Plane))
                {
                    EngineAutoGroup.Aircraft plane;
                    if (!aircraft.TryGetValue(row.Plane, out plane))
                        continue;
                    request.Aircraft[row.Plane] = new SetupAircraft { Key = row.Plane, Game = game, Type = plane.Type, Era = plane.Era, VariantOf = plane.VariantOf };
                }
                request.Commands.Add(new SetupCommand { Game = game, Aircraft = row.Plane, Function = row.Slot, Action = row.Action, Status = "map" });
                added++;
            }
            // The map's own warnings (game lists of other games not on this PC) are not the plan's business.
            if (added > 0)
                request.Notes.Add("Function map: " + added.ToString(CultureInfo.InvariantCulture) + " more actions the catalog does not list.");
        }

        // IL-2 takes a key or a joystick button on its plane-control axes too (its own default binds the key "/" to
        // rpc_wheel_brakes): the wheel brake button may use that axis command (the export writes joyN_bM on it).
        // "same-button" (0, the same function on the same button in every aircraft), "real-aircraft" (1, where the real
        // aircraft has it) or a position 0..1 / 0..100 %.
        // The open profile's head-tracking answer (Pr0file.HeadTracking): "yes", "no" or empty (not asked).
        internal static string ProfileHeadTrackingAnswer()
        {
            string value = (InternalDataManagement.HeadTracking ?? string.Empty).Trim().ToLowerInvariant();
            return value == "yes" || value == "no" ? value : string.Empty;
        }

        // With head tracking or VR no relation for looking around (snap views, look axes) is made or suggested in the sims
        // that support it (the recenter stays): Auto Group and the suggestions ask this.
        internal static bool SkipForHeadTracking(string function, string game)
        {
            return ProfileHeadTrackingAnswer() == "yes" && SetupPlanner.HeadTrackingLook.IsMatch(function ?? string.Empty) && SetupPlanner.HeadTrackingGame(game);
        }

        // yes / no (true / false, on / off, 1 / 0) for --head-tracking.
        internal static bool TryHeadTracking(string text, out bool value)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "yes": case "true": case "on": case "1": case "planned":
                    value = true;
                    return true;
                case "no": case "false": case "off": case "0":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        internal static bool TryPlacement(string text, out double value)
        {
            value = 0.5;
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t == "same-button" || t == "same" || t == "function")
            {
                value = 0;
                return true;
            }
            if (t == "real-aircraft" || t == "real" || t == "cockpit")
            {
                value = 1;
                return true;
            }
            double parsed;
            if (!double.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > 100)
                return false;
            value = parsed > 1 || t.EndsWith("%", StringComparison.Ordinal) ? parsed / 100.0 : parsed;
            return true;
        }

        internal static bool KeyOnAxisCommand(string game, string action, SetupFunction function)
        {
            return function != null && !function.IsAxis && string.Equals(function.Id, "wheelbrakes_button", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(game, "IL2Game", StringComparison.OrdinalIgnoreCase) || string.Equals(game, "IL2Korea", StringComparison.OrdinalIgnoreCase)) &&
                   string.Equals(action, "rpc_wheel_brakes", StringComparison.OrdinalIgnoreCase);
        }

        internal static string FunctionsFolder()
        {
            return EngineNlGroup.FunctionsFolder();
        }

        // A device named in the profile -> the device of the setup: same name, else the only one with that product name.
        private static string MapDevice(string profileDevice, List<SetupDevice> devices)
        {
            if (string.IsNullOrWhiteSpace(profileDevice) || devices.Any(d => d.Device == profileDevice))
                return profileDevice ?? string.Empty;
            string product = Product(profileDevice);
            List<SetupDevice> same = devices.Where(d => Product(d.Device) == product).ToList();
            return same.Count == 1 ? same[0].Device : profileDevice;
        }

        // The aircraft the user can put in the priority list: the chosen games' aircraft the local database has.
        public static List<SetupAircraft> Aircraft(List<string> games, List<string> warnings)
        {
            Dictionary<string, SetupFunction> functions;
            Dictionary<string, SetupAircraft> aircraft;
            List<SetupCommand> commands;
            if (!SetupCatalog.Load(FunctionsFolder(), warnings, out functions, out aircraft, out commands))
                return new List<SetupAircraft>();
            Dictionary<string, HashSet<string>> dbIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> withCommands = new HashSet<string>(commands.Select(c => c.Aircraft), StringComparer.OrdinalIgnoreCase);
            return SetupCatalog.AircraftOf(aircraft, games, a => withCommands.Contains(a.Key) && (!EngineAutoGroup.IsDcs(a.Game) || EngineAutoGroup.DatabaseIds(dbIds, a.Key).Count > 0));
        }

        // The profile's binds on the connected devices as a plan (the layout preview after the setup): every bound relation
        // on a device JoyPro knows, with the function name the catalog gives its commands.
        public static SetupPlan ProfilePlan(List<string> warnings)
        {
            List<SetupInput> inputs;
            List<SetupDevice> devices = Devices(new DeviceChoices(), warnings, out inputs);
            SetupOptions options = new SetupOptions { ExistingOnly = true };
            SetupRequest request = Request(devices, inputs, options, null, warnings);
            SetupPlan plan = new SetupPlan { Devices = devices, Inputs = inputs };
            foreach (SetupModifierChoice modifier in request.ExistingModifiers.Where(m => devices.Any(d => d.Device == m.Device)))
            {
                SetupInput input = inputs.FirstOrDefault(i => i.Device == modifier.Device && string.Equals(i.Input, modifier.Input, StringComparison.OrdinalIgnoreCase));
                plan.Modifiers.Add(new SetupPlanModifier { Name = modifier.Name, Device = modifier.Device, Input = modifier.Input, Control = input != null ? input.Control : string.Empty, Existing = true });
            }
            foreach (SetupExistingRelation existing in request.Existing.Where(e => e.OnSetup && e.Device.Length > 0 && e.Input.Length > 0))
            {
                SetupInput input = inputs.FirstOrDefault(i => i.Device == existing.Device && string.Equals(i.Input, existing.Input, StringComparison.OrdinalIgnoreCase));
                SetupFunction function;
                string name = existing.Function.Length > 0 && request.Functions.TryGetValue(existing.Function, out function) && function.Name.Length > 0 ? function.Name : existing.Name;
                plan.Relations.Add(new SetupPlanRelation
                {
                    Name = existing.Name, Function = existing.Function, FunctionName = name, IsAxis = existing.IsAxis, Existing = true, Kept = true,
                    Nodes = existing.Nodes, Aircraft = existing.Nodes.Select(n => n.Aircraft).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Device = existing.Device, Input = existing.Input, Control = input != null ? input.Control : string.Empty, Label = input != null ? input.Label : string.Empty,
                    Modifier = existing.Modifier, Reason = "your bind"
                });
            }
            return plan;
        }

        // ---- apply ----

        internal sealed class ApplyResult
        {
            public int Created;
            public int Extended;
            public int Bound;
            public int NodesAdded;
            public int ModifiersAdded;
            public List<string> Skipped = new List<string>();
            public List<string> Problems = new List<string>();
        }

        // The function catalog's own rows (DB/Functions, not the map's guesses and not substitutes): plane|action|slot.
        private static HashSet<string> catalogRows;
        private static readonly object CatalogRowsSync = new object();

        internal static bool CatalogFiles(string plane, string action, string function)
        {
            if (string.IsNullOrEmpty(plane) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(function))
                return false;
            lock (CatalogRowsSync)
            {
                if (catalogRows == null)
                {
                    HashSet<string> rows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, SetupFunction> functions;
                    Dictionary<string, SetupAircraft> aircraft;
                    List<SetupCommand> commands;
                    if (SetupCatalog.Load(FunctionsFolder(), new List<string>(), out functions, out aircraft, out commands))
                    {
                        foreach (SetupCommand command in commands.Where(c => !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase) &&
                                                                             !string.Equals(c.Status, "map", StringComparison.OrdinalIgnoreCase)))
                            rows.Add(command.Aircraft + "|" + command.Action + "|" + command.Function);
                    }
                    catalogRows = rows;
                }
                return catalogRows.Contains(plane + "|" + action + "|" + function);
            }
        }

        public static ApplyResult Apply(SetupPlan plan, bool dryRun, bool force)
        {
            ApplyResult result = new ApplyResult();
            result.Problems.AddRange(SetupPlanEdit.Validate(plan));
            if (result.Problems.Count > 0 && !force)
                return result;

            // A command a relation of the profile already uses stays there (it would make two relations fight).
            Dictionary<string, string> used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
            {
                foreach (RelationItem node in (pair.Value == null ? null : pair.Value.NODES) ?? new List<RelationItem>())
                {
                    if (node == null)
                        continue;
                    string game = EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game;
                    if (game == "DCS")
                    {
                        foreach (KeyValuePair<string, bool> plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()))
                            used[SetupPlanner.CommandKey(game, node.ID, plane.Key)] = pair.Key;
                    }
                    else
                    {
                        used[SetupPlanner.CommandKey(game, node.ID, game)] = pair.Key;
                    }
                }
            }

            Dictionary<string, Modifier> modifiers = new Dictionary<string, Modifier>(StringComparer.Ordinal);
            foreach (SetupPlanModifier planned in plan.Modifiers.Where(m => plan.Placed.Any(r => !r.Kept && r.Modifier == m.Name)))
            {
                Modifier modifier;
                if (!InternalDataManagement.AllModifiers.TryGetValue(planned.Name, out modifier) || modifier == null)
                {
                    modifier = new Modifier { name = planned.Name, device = planned.Device, key = planned.Input, sw = planned.Switch };
                    if (!dryRun)
                        InternalDataManagement.AllModifiers[planned.Name] = modifier;
                    result.ModifiersAdded++;
                }
                modifiers[planned.Name] = modifier;
            }

            foreach (SetupPlanRelation planned in plan.Relations.Where(r => !r.Removed))
            {
                bool bindIt = !planned.Kept && planned.Device.Length > 0 && planned.Input.Length > 0;
                if (!bindIt && (planned.Nodes.Count == 0 || !planned.Kept))
                    continue;
                Relation relation;
                bool exists = InternalDataManagement.AllRelations.TryGetValue(planned.Name, out relation) && relation != null;
                if (exists && relation.ISAXIS != planned.IsAxis)
                {
                    result.Skipped.Add(planned.Name + ": the profile has it as " + (relation.ISAXIS ? "an axis" : "a button") + " now");
                    continue;
                }
                if (!exists)
                {
                    if (planned.Nodes.Count == 0)
                    {
                        result.Skipped.Add(planned.Name + ": no longer in the profile");
                        continue;
                    }
                    relation = new Relation { NAME = planned.Name, ISAXIS = planned.IsAxis, Groups = new List<string> { "Setup" }, NODES = new List<RelationItem>() };
                }
                int added = 0;
                // Per DCS id: aircraft where the id means something else than in the relation's other aircraft are
                // left out (switched off in the node, one tick brings them back; EngineIdMeanings.Guard).
                HashSet<string> meaningOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IGrouping<string, SetupNode> sameId in planned.Nodes.Where(n => EngineAutoGroup.IsDcs(n.Game)).GroupBy(n => n.Action, StringComparer.OrdinalIgnoreCase))
                {
                    List<string> excluded;
                    List<string> notes;
                    EngineIdMeanings.Guard("DCS", sameId.Key, sameId.Select(n => n.Aircraft), planned.Name, out excluded, out notes);
                    foreach (string plane in excluded)
                    {
                        // Review 6 B9: the function catalog itself files this aircraft's command as the relation's function
                        // (the Mi-8's cyclic FIRE button and the gunner's door gun share the id d84pnilu84 and are both
                        // trigger2ndstage): the planner placed it on purpose, the meaning check must not switch it off - it
                        // was the Mi-8 pilot's only gun bind.
                        if (CatalogFiles(plane, sameId.Key, planned.Function))
                            continue;
                        meaningOut.Add(sameId.Key + "|" + plane);
                        result.Skipped.Add(planned.Name + ": " + (notes.FirstOrDefault(n => n.StartsWith(plane + " ", StringComparison.OrdinalIgnoreCase)) ?? plane + " " + sameId.Key) + " (left out, switched off)");
                    }
                }
                foreach (SetupNode node in planned.Nodes)
                {
                    string game = EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game;
                    if (game == "DCS" && meaningOut.Contains(node.Action + "|" + node.Aircraft))
                    {
                        if (!dryRun && relation.AddNode(node.Action, node.Game, planned.IsAxis, node.Aircraft))
                        {
                            RelationItem off = relation.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, node.Action, StringComparison.OrdinalIgnoreCase) && EngineAutoGroup.IsDcs(n.Game ?? "DCS"));
                            if (off != null)
                                off.SetAircraftActivity(node.Aircraft, false);
                        }
                        continue;
                    }
                    string owner;
                    if (used.TryGetValue(SetupPlanner.CommandKey(game, node.Action, SetupPlanner.NodeKey(node)), out owner) && !string.Equals(owner, planned.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped.Add(node.Aircraft + " " + node.Action + " stays in '" + owner + "'");
                        continue;
                    }
                    if (dryRun || relation.AddNode(node.Action, node.Game, planned.IsAxis, node.Aircraft))
                        added++;
                }
                if (!exists && added == 0)
                {
                    result.Skipped.Add(planned.Name + ": every command is in another relation now");
                    continue;
                }
                result.NodesAdded += added;
                if (!exists)
                {
                    result.Created++;
                    if (!dryRun)
                        InternalDataManagement.AllRelations[relation.NAME] = relation;
                }
                else if (added > 0)
                {
                    result.Extended++;
                }
                if (!bindIt)
                    continue;
                result.Bound++;
                if (dryRun)
                    continue;
                // A relation that had a bind keeps its axis tuning (base curve, per game / aircraft tuning) on the new input.
                Bind old;
                InternalDataManagement.AllBinds.TryGetValue(relation.NAME, out old);
                Bind bind = old != null && old.Joystick != null ? old.Copy(relation) : new Bind(relation);
                bind.Joystick = planned.Device;
                bind.JoyBackup = planned.Device;
                if (old != null && !string.Equals(old.Joystick, planned.Device, StringComparison.Ordinal))
                    bind.AliasJoystick = string.Empty;
                if (planned.IsAxis)
                {
                    bind.JAxis = planned.Input;
                    bind.JButton = string.Empty;
                }
                else
                {
                    bind.JButton = planned.Input;
                    bind.JAxis = string.Empty;
                }
                Modifier modifier;
                bind.AllReformers = planned.Modifier.Length > 0 && modifiers.TryGetValue(planned.Modifier, out modifier) ? new List<string> { modifier.toReformerString() } : new List<string>();
                relation.bind = bind;
                InternalDataManagement.AllBinds[relation.NAME] = bind;
            }
            return result;
        }

        // ---- callout sheets of the plan ----

        public static List<KeyValuePair<string, string>> Render(SetupPlan plan, string outDir, string aircraft, bool light, int width, List<string> warnings)
        {
            List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
            Directory.CreateDirectory(outDir);
            DeviceCatalog catalog = Program.LoadReferenceCatalog();
            int index = 0;
            foreach (SetupDevice device in plan.Devices.Where(d => SetupRoles.Binds(d.Role)))
            {
                index++;
                string modelId = device.Grip.Length > 0 ? device.Grip : device.ModelId;
                RefDevice model = modelId.Length > 0 ? catalog.Find(modelId) : null;
                if (model == null)
                {
                    warnings.Add(SetupRoles.Short(device) + ": no drawing (not in the device catalog); see the table.");
                    continue;
                }
                List<SetupPlanRelation> mine = plan.Placed.Where(r => r.Device == device.Device && (string.IsNullOrEmpty(aircraft) || r.Aircraft.Contains(aircraft, StringComparer.OrdinalIgnoreCase) ||
                                                                                                  r.Overlap.Contains(aircraft, StringComparer.OrdinalIgnoreCase))).ToList();
                Func<string, bool, string> text = (input, withModifier) =>
                {
                    List<string> labels = mine.Where(r => string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase) && (r.Modifier.Length > 0) == withModifier)
                        .OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => (withModifier && plan.Modifiers.Count > 1 ? ShortModifier(plan, r.Modifier) + ": " : string.Empty) +
                                     (string.IsNullOrEmpty(aircraft) || r.FunctionName.Length == 0 ? r.Name : r.FunctionName))
                        .Distinct().ToList();
                    return string.Join(" / ", labels);
                };
                Bitmap picture = null;
                try
                {
                    // JoyPro's line drawing of the model with its shipped sheet when there is one, else its schematic.
                    DeviceLayoutSet set = Program.LineArtSetOf(model.Id);
                    CalloutSheet sheet = set != null ? set.Callouts : null;
                    if (sheet == null)
                    {
                        DeviceSchematic drawing = SchematicBuilder.Build(model, catalog.BaseOf(model));
                        set = new DeviceLayoutSet { Model = model.Id, PhotoKind = "drawing" };
                        foreach (SchematicMarker marker in drawing.Markers)
                            set.Markers[marker.Id] = new[] { marker.X, marker.Y };
                        sheet = CalloutLayouter.Arrange(drawing, set.Markers, drawing.Width, drawing.Height, null);
                    }
                    sheet.Modifier = plan.Modifiers.Count == 1 ? plan.Modifiers[0].Name : plan.Modifiers.Count > 1 ? "Modifiers" : string.Empty;
                    picture = EngineCalloutRenderer.LoadPicture(set, sheet, null, !light, warnings);
                    CalloutRenderOptions options = new CalloutRenderOptions
                    {
                        Dark = !light,
                        PrintWidth = Math.Max(0, Math.Min(6000, width)),
                        // The footer names the plan's games ("GAME: IL-2 Sturmovik: Great Battles"), never "Setup plan".
                        Game = string.IsNullOrEmpty(aircraft) ? PlanGameNames(plan) : GameOf(plan, aircraft),
                        Plane = string.IsNullOrEmpty(aircraft) ? "All aircraft" : aircraft,
                        Device = SetupRoles.Short(device) + " - " + SetupRoles.Title(device.Role)
                    };
                    EngineCalloutRenderer.ApplyStyle(options, set);
                    string file = Path.Combine(outDir, index.ToString("D2", CultureInfo.InvariantCulture) + "-" + Safe(SetupRoles.Short(device)) + (string.IsNullOrEmpty(aircraft) ? string.Empty : "-" + Safe(aircraft)) + ".png");
                    using (Bitmap rendered = EngineCalloutRenderer.Render(sheet, picture, text, options))
                        rendered.Save(JoyPro.Engine.TestSandbox.EnsureWritable(file, "Image save"), ImageFormat.Png);
                    files.Add(new KeyValuePair<string, string>(device.Device, file));
                }
                catch (Exception ex)
                {
                    warnings.Add(SetupRoles.Short(device) + ": the sheet could not be drawn: " + ex.Message);
                }
                finally
                {
                    if (picture != null)
                        picture.Dispose();
                }
            }
            return files;
        }

        // The layout window's live view (docs/setup-wizard.md#layout-window): per device of the plan the drawing alone
        // (<n>-<device>.picture.png), its callout sheet (<n>-<device>.sheet.json, CalloutSheet format) and what each row
        // shows for the aircraft (input -> texts of the main and the modifier column). The GUI draws the sheet itself
        // (CalloutSheetCanvas), so the row of a pressed input can light up.
        public static List<Dictionary<string, object>> LiveData(SetupPlan plan, string outDir, string aircraft, bool light, List<string> warnings)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Directory.CreateDirectory(outDir);
            DeviceCatalog catalog = Program.LoadReferenceCatalog();
            int index = 0;
            foreach (SetupDevice device in plan.Devices.Where(d => SetupRoles.Binds(d.Role)))
            {
                index++;
                string modelId = device.Grip.Length > 0 ? device.Grip : device.ModelId;
                RefDevice model = modelId.Length > 0 ? catalog.Find(modelId) : null;
                if (model == null)
                {
                    warnings.Add(SetupRoles.Short(device) + ": no drawing (not in the device catalog).");
                    continue;
                }
                List<SetupPlanRelation> mine = plan.Placed.Where(r => r.Device == device.Device && (string.IsNullOrEmpty(aircraft) || r.Aircraft.Contains(aircraft, StringComparer.OrdinalIgnoreCase) ||
                                                                                                  r.Overlap.Contains(aircraft, StringComparer.OrdinalIgnoreCase))).ToList();
                Func<string, bool, string> text = TextOf(plan, mine, aircraft);
                Bitmap picture = null;
                try
                {
                    DeviceLayoutSet set = Program.LineArtSetOf(model.Id);
                    CalloutSheet sheet = set != null ? set.Callouts : null;
                    if (sheet == null)
                    {
                        DeviceSchematic drawing = SchematicBuilder.Build(model, catalog.BaseOf(model));
                        set = new DeviceLayoutSet { Model = model.Id, PhotoKind = "drawing" };
                        foreach (SchematicMarker marker in drawing.Markers)
                            set.Markers[marker.Id] = new[] { marker.X, marker.Y };
                        sheet = CalloutLayouter.Arrange(drawing, set.Markers, drawing.Width, drawing.Height, null);
                    }
                    sheet = sheet.Clone();
                    sheet.Modifier = plan.Modifiers.Count == 1 ? plan.Modifiers[0].Name : plan.Modifiers.Count > 1 ? "Modifiers" : string.Empty;
                    sheet.Combos.Clear();
                    picture = EngineCalloutRenderer.LoadPicture(set, sheet, null, !light, warnings);
                    string stem = Path.Combine(outDir, index.ToString("D2", CultureInfo.InvariantCulture) + "-" + Safe(SetupRoles.Short(device)));
                    string pictureFile = string.Empty;
                    if (picture != null)
                    {
                        pictureFile = stem + ".picture.png";
                        picture.Save(JoyPro.Engine.TestSandbox.EnsureWritable(pictureFile, "Image save"), ImageFormat.Png);
                    }
                    string sheetFile = stem + ".sheet.json";
                    File.WriteAllText(JoyPro.Engine.TestSandbox.EnsureWritable(sheetFile, "Sheet save"), sheet.ToJson(), new UTF8Encoding(false));
                    Dictionary<string, object> texts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (string input in sheet.Tables.SelectMany(t => t.Rows).Select(r => r.Input).Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string main = text(input, false), shifted = text(input, true);
                        if (main.Length > 0 || shifted.Length > 0)
                            texts[input] = new List<object> { main, shifted };
                    }
                    list.Add(new Dictionary<string, object>
                    {
                        { "Device", device.Device }, { "Name", SetupRoles.Short(device) }, { "Role", device.Role }, { "Model", model.Id },
                        { "Picture", pictureFile }, { "Sheet", sheetFile }, { "Texts", texts }
                    });
                }
                catch (Exception ex)
                {
                    warnings.Add(SetupRoles.Short(device) + ": the sheet could not be made: " + ex.Message);
                }
                finally
                {
                    if (picture != null)
                        picture.Dispose();
                }
            }
            return list;
        }

        // What a row shows: the functions on the input (with the modifier's short name in the modifier column when the
        // plan has several), one aircraft's function names or the relation names.
        private static Func<string, bool, string> TextOf(SetupPlan plan, List<SetupPlanRelation> mine, string aircraft)
        {
            return (input, withModifier) =>
            {
                List<string> labels = mine.Where(r => string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase) && (r.Modifier.Length > 0) == withModifier)
                    .OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => (withModifier && plan.Modifiers.Count > 1 ? ShortModifier(plan, r.Modifier) + ": " : string.Empty) +
                                 (string.IsNullOrEmpty(aircraft) || r.FunctionName.Length == 0 ? r.Name : r.FunctionName))
                    .Distinct().ToList();
                return string.Join(" / ", labels);
            };
        }

        private static string GameOf(SetupPlan plan, string aircraft)
        {
            SetupQuality quality = plan.Quality.FirstOrDefault(q => string.Equals(q.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase));
            return quality != null ? quality.Game : string.Empty;
        }

        // The plan's games by their names (DCS World, IL-2 Sturmovik: Great Battles ...), joined.
        private static string PlanGameNames(SetupPlan plan)
        {
            List<string> ids = plan.Games.Count > 0 ? plan.Games : plan.Quality.Select(q => q.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return string.Join(", ", ids.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g =>
            {
                IGameIntegration integration;
                return EngineAutoGroup.IsDcs(g) ? "DCS World" : GameRegistry.TryResolve(g, out integration) ? integration.DisplayName : g;
            }).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string ShortModifier(SetupPlan plan, string name)
        {
            int index = plan.Modifiers.FindIndex(m => m.Name == name);
            return index >= 0 ? "S" + (index + 1).ToString(CultureInfo.InvariantCulture) : name;
        }

        private static string Safe(string name)
        {
            string safe = Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
            return safe.Length > 40 ? safe.Substring(0, 40) : safe;
        }
    }

    internal static partial class Program
    {
        // engine setup devices [--grip <device>=<grip id>]... [--role <device>=<role>]... [--add-device <catalog id>]...
        // engine setup aircraft --games <g1,g2>
        // engine setup plan --games <g1,g2> [--priorities "A>B|C>D"] [--priority "B|C"]... [--modifiers N] [--prefer-fewer|--use-all-modifiers]
        //     [--realism 0..1] [--seed N] [--replace-existing] [--phase-layers on|off|auto] [--role ...] [--grip ...] [--add-device ...] [--cockpit-file <json>]...
        //     [--out-plan <plan.json>] (also --plan-file)
        // engine setup check --plan <plan.json> [--modifiers N]
        // engine setup apply --plan <plan.json> [--dry-run] [--force]
        // engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]
        private static int RunSetup(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "devices" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            List<string> warnings = new List<string>();
            switch (action)
            {
                case "devices":
                {
                    EngineSetupWizard.DeviceChoices choices;
                    string error = ParseSetupDeviceChoices(args, out choices);
                    if (error != null)
                        return Fail(error);
                    if (args.Count > 0)
                        return Fail("Unknown setup devices argument(s): " + string.Join(" ", args));
                    List<SetupInput> inputs;
                    List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Setup Devices: " + devices.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (SetupDevice device in devices)
                        {
                            sb.AppendLine("  - " + device.Device + " => " + device.Role + (device.Model.Length > 0 ? " | " + device.Model : string.Empty) + " | " + device.Kind +
                                          (device.Hand != "none" && device.Hand.Length > 0 ? " | " + device.Hand + " hand" : string.Empty) + (device.ForceFeedback ? " | FFB" : string.Empty) +
                                          (device.NeedsGrip ? " | grip needed" : string.Empty) + " | " + inputs.Count(i => i.Device == device.Device).ToString(CultureInfo.InvariantCulture) + " inputs" +
                                          (device.Reason.Length > 0 ? " - " + device.Reason : string.Empty));
                            foreach (SetupGripChoice grip in device.Grips)
                                sb.AppendLine("      grip: " + grip.Id + " | " + grip.Name);
                        }
                        sb.AppendLine("Rules:");
                        foreach (string rule in SetupRoles.Rules(devices))
                            sb.AppendLine("  - " + rule);
                        foreach (string warning in warnings.Distinct())
                            sb.AppendLine("Warning: " + warning);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        Dictionary<string, object> root = new Dictionary<string, object>
                        {
                            { "Devices", devices.Select(d =>
                                {
                                    Dictionary<string, object> item = SetupPlanJson.DeviceObject(d);
                                    item["Inputs"] = inputs.Count(i => i.Device == d.Device);
                                    item["RoleTitle"] = SetupRoles.Title(d.Role);
                                    return (object)item;
                                }).ToList() },
                            { "Rules", SetupRoles.Rules(devices).Cast<object>().ToList() },
                            { "Roles", SetupRoles.All.Select(r => (object)new Dictionary<string, object> { { "Id", r }, { "Title", SetupRoles.Title(r) } }).ToList() },
                            { "Warnings", warnings.Distinct().Cast<object>().ToList() }
                        };
                        EmitPayload(MiniJson.Write(root, false), outPath);
                    }
                    return 0;
                }
                case "aircraft":
                {
                    List<string> games = SplitList(PopOptionValue(args, "--games"));
                    if (args.Count > 0)
                        return Fail("Unknown setup aircraft argument(s): " + string.Join(" ", args));
                    if (games.Count == 0)
                        return Fail("setup aircraft needs --games <game ids>.");
                    List<SetupAircraft> aircraft = EngineSetupWizard.Aircraft(games, warnings);
                    Dictionary<string, string> installed = aircraft.Any(a => EngineAutoGroup.IsDcs(a.Game)) ? InstalledDcsModules(warnings) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Setup Aircraft: " + aircraft.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (SetupAircraft plane in aircraft)
                            sb.AppendLine("  - " + plane.Key + " | " + plane.Game + " | " + SetupClasses.Title(plane.Class));
                        foreach (string warning in warnings.Distinct())
                            sb.AppendLine("Warning: " + warning);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        Dictionary<string, object> root = new Dictionary<string, object>
                        {
                            { "Aircraft", aircraft.Select(a =>
                                {
                                    Dictionary<string, object> entry = new Dictionary<string, object>
                                    {
                                        { "Key", a.Key }, { "Game", a.Game }, { "Type", a.Type }, { "Class", a.Class }, { "ClassTitle", SetupClasses.Title(a.Class) }, { "Era", a.Era }
                                    };
                                    foreach (KeyValuePair<string, object> part in SetupAircraftChoice(a, installed))
                                        entry[part.Key] = part.Value;
                                    return (object)entry;
                                }).ToList() },
                            { "Warnings", warnings.Distinct().Cast<object>().ToList() }
                        };
                        EmitPayload(MiniJson.Write(root, false), outPath);
                    }
                    return 0;
                }
                case "plan":
                {
                    StartClean();
                    EngineSetupWizard.DeviceChoices choices;
                    string error = ParseSetupDeviceChoices(args, out choices);
                    if (error != null)
                        return Fail(error);
                    SetupOptions options = new SetupOptions();
                    options.Games = SplitList(PopOptionValue(args, "--games"));
                    string priorities = PopOptionValue(args, "--priorities");
                    if (!string.IsNullOrWhiteSpace(priorities))
                        options.Priorities.AddRange(priorities.Split('>').Select(SplitTier).Where(t => t.Count > 0));
                    foreach (string tier in PopOptionValues(args, "--priority"))
                    {
                        List<string> parsed = SplitTier(tier);
                        if (parsed.Count > 0)
                            options.Priorities.Add(parsed);
                    }
                    string modifiers = PopOptionValue(args, "--modifiers");
                    if (modifiers != null)
                    {
                        int parsed;
                        if (!int.TryParse(modifiers, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > 8)
                            return Fail("--modifiers takes 0 to 8.");
                        options.Modifiers = parsed;
                    }
                    bool preferFewer = PopFlag(args, "--prefer-fewer");
                    bool useAll = PopFlag(args, "--use-all-modifiers");
                    if (preferFewer && useAll)
                        return Fail("--prefer-fewer and --use-all-modifiers exclude each other.");
                    options.PreferFewer = !useAll;
                    // Where functions go: --placement same-button | real-aircraft | <0..1> (--realism is the old name of it).
                    string realism = PopOptionValue(args, "--placement") ?? PopOptionValue(args, "--realism");
                    if (realism != null)
                    {
                        double parsed;
                        if (!EngineSetupWizard.TryPlacement(realism, out parsed))
                            return Fail("--placement takes same-button, real-aircraft or 0..1 (or 0..100 %).");
                        options.Realism = parsed;
                    }
                    options.Seed = PopOptionInt(args, "--seed", 1);
                    // Head tracking / VR (or planned): no inputs for looking around in the sims that support it.
                    // Without the option the profile's answer (an earlier wizard, engine profile head-tracking) holds.
                    string headTracking = PopOptionValue(args, "--head-tracking");
                    string headTrackingAnswer = EngineSetupWizard.ProfileHeadTrackingAnswer();
                    if (headTracking != null)
                    {
                        bool parsedTracking;
                        if (!EngineSetupWizard.TryHeadTracking(headTracking, out parsedTracking))
                            return Fail("--head-tracking takes yes or no.");
                        headTrackingAnswer = parsedTracking ? "yes" : "no";
                    }
                    options.HeadTracking = headTrackingAnswer == "yes";
                    options.OnlyAircraft = SplitList(PopOptionValue(args, "--only-aircraft"));
                    options.ReplaceExisting = PopFlag(args, "--replace-existing");
                    string phaseLayers = PopOptionValue(args, "--phase-layers");
                    if (phaseLayers != null && !Regex.IsMatch(phaseLayers.Trim(), "^(on|off|auto)$", RegexOptions.IgnoreCase))
                        return Fail("--phase-layers takes on, off or auto.");
                    options.PhaseLayers = phaseLayers ?? "auto";
                    List<string> cockpitFiles = PopOptionValues(args, "--cockpit-file").Select(NormalizePath).ToList();
                    string save = PopOptionValue(args, "--out-plan") ?? PopOptionValue(args, "--plan-file");
                    if (args.Count > 0)
                        return Fail("Unknown setup plan argument(s): " + string.Join(" ", args));
                    if (options.Games.Count == 0)
                        return Fail("setup plan needs --games <game ids> (the games ticked for watching on the Start page).");

                    List<SetupInput> inputs;
                    List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                    if (!devices.Any(d => SetupRoles.Binds(d.Role)))
                        return Fail("No device to plan with: connect your devices (or give them a role with --role).");
                    SetupRequest request = EngineSetupWizard.Request(devices, inputs, options, cockpitFiles, warnings);
                    SetupPlan plan = SetupPlanner.Plan(request);
                    plan.HeadTracking = headTrackingAnswer;
                    EngineSetupWizard.FlightAxesGuard(plan, request);
                    string json = SetupPlanJson.Write(plan, false);
                    if (!string.IsNullOrWhiteSpace(save))
                    {
                        string target = NormalizePath(save);
                        string directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);
                        File.WriteAllText(target, SetupPlanJson.Write(plan, true), new UTF8Encoding(false));
                    }
                    EmitPayload(textOutput ? SetupPlanText.Write(plan, 40) : json, outPath);
                    return 0;
                }
                case "check":
                {
                    // The rules every plan must keep (SetupPlanChecks): conflicts, modifiers, dedicated controls, MFDs,
                    // printed labels, slew vs TMS, CMS, one device per hand, phase layers, games without modifier binds.
                    string file = PopOptionValue(args, "--plan");
                    int allowed = PopOptionInt(args, "--modifiers", 2);
                    if (args.Count > 0)
                        return Fail("Unknown setup check argument(s): " + string.Join(" ", args));
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(NormalizePath(file)))
                        return Fail("setup check needs --plan <plan.json> (from engine setup plan --out-plan).");
                    SetupPlan plan;
                    try
                    {
                        plan = SetupPlanJson.Read(File.ReadAllText(NormalizePath(file), Encoding.UTF8));
                    }
                    catch (Exception ex)
                    {
                        return Fail("The plan could not be read: " + ex.Message);
                    }
                    Dictionary<string, SetupFunction> functions;
                    Dictionary<string, SetupAircraft> catalogAircraft;
                    List<SetupCommand> commands;
                    List<string> errors = new List<string>();
                    SetupCatalog.Load(EngineSetupWizard.FunctionsFolder(), errors, out functions, out catalogAircraft, out commands);
                    Dictionary<string, string> support = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string game in plan.Games.Concat(plan.Relations.SelectMany(r => r.Nodes).Select(n => n.Game)).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
                        support[game] = EngineGameSupport.ModifierSupport(game);
                    SetupCheckReport report = SetupPlanChecks.Check(plan, functions, support, Math.Max(allowed, plan.ModifiersWanted));
                    EmitPayload(textOutput ? SetupPlanChecks.ToText(report) : EngineSerialization.ToJson(report), outPath);
                    // Exit code 1 when a rule failed (the report says which), as for db check.
                    return report.Passed ? 0 : 1;
                }
                case "apply":
                {
                    string file = PopOptionValue(args, "--plan");
                    bool dryRun = PopFlag(args, "--dry-run");
                    bool force = PopFlag(args, "--force");
                    if (args.Count > 0)
                        return Fail("Unknown setup apply argument(s): " + string.Join(" ", args));
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(NormalizePath(file)))
                        return Fail("setup apply needs --plan <plan.json> (from engine setup plan --out-plan).");
                    SetupPlan plan;
                    try
                    {
                        plan = SetupPlanJson.Read(File.ReadAllText(NormalizePath(file), Encoding.UTF8));
                    }
                    catch (Exception ex)
                    {
                        return Fail("The plan could not be read: " + ex.Message);
                    }
                    EngineSetupWizard.ApplyResult result = EngineSetupWizard.Apply(plan, dryRun, force);
                    if (result.Problems.Count > 0 && !force)
                        return Fail("The plan puts functions of the same aircraft on one input:" + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Select(p => "  - " + p)) +
                                    Environment.NewLine + "Fix them in the review (or apply with --force).");
                    if (!dryRun)
                    {
                        // The head-tracking answer the plan was made with belongs to the profile (Auto Group, suggestions).
                        if (plan.HeadTracking == "yes" || plan.HeadTracking == "no")
                            InternalDataManagement.HeadTracking = plan.HeadTracking;
                        InternalDataManagement.ResyncBindsToMods();
                        InternalDataManagement.ResyncRelations();
                        InternalDataManagement.RecalcFigures();
                        // One version in the profile history for the whole setup (Undo takes all of it back).
                        EngineProfileHistory.Note = "Setup wizard: " + (result.Created + result.Extended).ToString(CultureInfo.InvariantCulture) + " relations, " +
                                                    result.Bound.ToString(CultureInfo.InvariantCulture) + " binds" +
                                                    (result.ModifiersAdded > 0 ? ", " + result.ModifiersAdded.ToString(CultureInfo.InvariantCulture) + " modifiers" : string.Empty);
                    }
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Setup Apply");
                        sb.AppendLine("Dry Run: " + dryRun);
                        sb.AppendLine("Relations Created: " + result.Created.ToString(CultureInfo.InvariantCulture));
                        sb.AppendLine("Relations Extended: " + result.Extended.ToString(CultureInfo.InvariantCulture));
                        sb.AppendLine("Commands Added: " + result.NodesAdded.ToString(CultureInfo.InvariantCulture));
                        sb.AppendLine("Binds Set: " + result.Bound.ToString(CultureInfo.InvariantCulture));
                        sb.AppendLine("Modifiers Added: " + result.ModifiersAdded.ToString(CultureInfo.InvariantCulture));
                        foreach (string skipped in result.Skipped.Take(50))
                            sb.AppendLine("Skipped: " + skipped);
                        foreach (string problem in result.Problems)
                            sb.AppendLine("Problem: " + problem);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(MiniJson.Write(new Dictionary<string, object>
                        {
                            { "DryRun", dryRun }, { "Created", result.Created }, { "Extended", result.Extended }, { "NodesAdded", result.NodesAdded },
                            { "Bound", result.Bound }, { "ModifiersAdded", result.ModifiersAdded }, { "Skipped", result.Skipped.Cast<object>().ToList() },
                            { "Problems", result.Problems.Cast<object>().ToList() }
                        }, false), outPath);
                    }
                    return 0;
                }
                case "render":
                {
                    string file = PopOptionValue(args, "--plan");
                    string outDir = PopOptionValue(args, "--out-dir");
                    string aircraft = PopOptionValue(args, "--aircraft") ?? string.Empty;
                    bool light = PopFlag(args, "--light");
                    int width = PopOptionInt(args, "--width", 1800);
                    DrawingStyle optionStyle = DrawingStyle.FromOptions(args, DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true));
                    if (optionStyle != null)
                        EngineCalloutRenderer.ExportStyle = optionStyle;
                    if (args.Count > 0)
                        return Fail("Unknown setup render argument(s): " + string.Join(" ", args));
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(NormalizePath(file)) || string.IsNullOrWhiteSpace(outDir))
                        return Fail("setup render needs --plan <plan.json> and --out-dir <folder>.");
                    SetupPlan plan;
                    try
                    {
                        plan = SetupPlanJson.Read(File.ReadAllText(NormalizePath(file), Encoding.UTF8));
                    }
                    catch (Exception ex)
                    {
                        return Fail("The plan could not be read: " + ex.Message);
                    }
                    List<KeyValuePair<string, string>> files = EngineSetupWizard.Render(plan, NormalizePath(outDir), aircraft, light, width, warnings);
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Setup Sheets: " + files.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (KeyValuePair<string, string> item in files)
                            sb.AppendLine("  - " + item.Key + " => " + item.Value);
                        foreach (string warning in warnings.Distinct())
                            sb.AppendLine("Warning: " + warning);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(MiniJson.Write(new Dictionary<string, object>
                        {
                            { "Sheets", files.Select(f => (object)new Dictionary<string, object> { { "Device", f.Key }, { "File", f.Value } }).ToList() },
                            { "Warnings", warnings.Distinct().Cast<object>().ToList() }
                        }, false), outPath);
                    }
                    return 0;
                }
                case "eval":
                    StartClean();
                    return RunSetupEval(args, outPath, textOutput);
                case "eval-pad":
                    StartClean();
                    return RunSetupEvalPad(args, outPath, textOutput);
                case "games":
                    return RunSetupGames(args, outPath, textOutput);
                case "grip-sweep":
                    StartClean();
                    return RunSetupGripSweep(args, outPath);
                case "preview":
                    return RunSetupPreview(args, outPath, textOutput);
                default:
                    return Fail("Unknown setup action: " + action + " (devices, aircraft, plan, check, apply, render, eval, eval-pad, games, preview, grip-sweep).");
            }
        }

        // engine setup games [--games <ids>] [--path <game>=<config folder>]... [--status-only]: per game whether the user
        // already has binds there (the Start page's auto setup, docs/setup-wizard.md#start-page). Read-only: the game's
        // control files are only read (engine profile game-state), the profile is not changed. --status-only (the app):
        // a game the profile already decides (binds in the profile, imported from or exported to it) keeps its control
        // files unread (GameBindsRead false, GameBinds 0): its status is "profile" whatever the game has. The connected
        // devices are read once for the whole run (each registry game asked Windows again, 0.2-0.4 s per game).
        //   empty   - nothing assigned: neither the game's own files nor the profile have binds for it -> set it up;
        //   game    - the game has binds of its own that the profile does not have -> import them first, then fill the gaps;
        //   profile - the profile has binds for it (or imported from / exported to it) -> only fill the gaps.
        // (harness loophole, 2026-11-16) Without an explicit --profile, setup plan / eval / eval-pad start from an empty
        // profile: before, they inherited the data folder's last-used profile and planned around its relations as "your
        // binds, kept". With --profile the given profile is used, as before.
        private static void StartClean()
        {
            if (string.IsNullOrWhiteSpace(EngineProfileHistory.ProfilePath))
                InternalDataManagement.NewFile(true);
        }

        // The quick path's aircraft question (docs/setup-wizard.md#quick-path) reads, per aircraft: the name a person
        // reads (DCS's own module name when the module is on this PC, else the cockpit database's variant name without its
        // seat, else the id made readable; a game's own entry is the game's name), the module it belongs to, whether that
        // module is on this PC, its rank among the popular DCS modules (-1: not among them) and whether it is a second seat
        // (gunner, RIO, WSO, the AI menu ...), which comes with its module instead of being offered on its own.
        internal static Dictionary<string, object> SetupAircraftChoice(SetupAircraft aircraft, Dictionary<string, string> installed)
        {
            bool dcs = EngineAutoGroup.IsDcs(aircraft.Game);
            string name;
            if (!dcs || string.Equals(aircraft.Key, aircraft.Game, StringComparison.OrdinalIgnoreCase))
                name = GameDisplayName(aircraft.Game);
            else if (installed.TryGetValue(aircraft.Key, out name) && !string.IsNullOrWhiteSpace(name))
                name = name.Trim();
            else if (CockpitUnitNames().TryGetValue("DCS|" + aircraft.Key, out name) && !string.IsNullOrWhiteSpace(name))
                name = WithoutSeat(name);
            else
                name = ReadableUnitId(aircraft.Key);
            return new Dictionary<string, object>
            {
                { "Name", name },
                { "Module", aircraft.Module.Length > 0 ? aircraft.Module : aircraft.Key },
                { "Installed", dcs && installed.ContainsKey(aircraft.Key) },
                { "Popular", dcs ? Array.FindIndex(EngineSetupEval.TypicalHangar, k => string.Equals(k, aircraft.Key, StringComparison.OrdinalIgnoreCase)) : -1 },
                { "Seat", dcs && IsSecondSeat(aircraft.Key) }
            };
        }

        // "F-14_RIO", "AH-64D_BLK_II_CPG", "Mi-24P_operator", "UH-1H_Gunner", "SA342 Copilot", "C-130J_UI", "AH-64D_BLK_II_AI_Menu".
        internal static bool IsSecondSeat(string key)
        {
            return Regex.IsMatch(key ?? string.Empty, @"(_(RIO|WSO|CPG|Gunner|TrackIR_Gunner|GUN|operator|AimingStation|LoadMaster|UI|AI_Menu)$)|(\s(Copilot|Gunner)(\s+Personal\s+Weapon)?$)", RegexOptions.IgnoreCase);
        }

        // "F-15E Strike Eagle (pilot)" -> "F-15E Strike Eagle", "Mi-24P, pilot" -> "Mi-24P".
        internal static string WithoutSeat(string name)
        {
            string text = Regex.Replace(name ?? string.Empty, @"\s*\((pilot|front seat)\)\s*$", string.Empty, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @",\s*pilot\s*$", string.Empty, RegexOptions.IgnoreCase);
            return text.Trim();
        }

        // The DCS modules on this PC (DCS's own names, entry.lua displayName) by aircraft key; none when DCS is not found.
        // Install detection off (tests): only a DCS folder the user set counts.
        private static Dictionary<string, string> InstalledDcsModules(List<string> warnings)
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) == "off" && GameInstalls.OverrideFolder("DCS") == null)
                return names;
            try
            {
                foreach (EngineMediaAircraft plane in EngineMedia.Aircraft(warnings))
                {
                    if (!names.ContainsKey(plane.Plane))
                        names[plane.Plane] = plane.DisplayName ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                warnings.Add("The DCS modules on this PC could not be read: " + ex.Message);
            }
            return names;
        }

        // The game's own title of each command of a game with an action list (review 7 B14 / L43): an id like IL-2 Cliffs of
        // Dover's "pilot.enginei01" does not say it is a toggle, its title "Engine start/stop" does. DCS keeps its ids.
        internal static void CommandTitles(List<SetupCommand> commands, List<string> warnings)
        {
            foreach (IGrouping<string, SetupCommand> game in commands.Where(c => !EngineAutoGroup.IsDcs(c.Game)).GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase))
            {
                IGameIntegration integration;
                if (!GameRegistry.TryResolve(game.Key, out integration))
                    continue;
                GameCatalog catalog = null;
                try
                {
                    catalog = GameCatalogStore.Load(integration, new List<string>());
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    warnings.Add(integration.DisplayName + ": action titles not read (" + ex.Message + ").");
                }
                if (catalog == null)
                    continue;
                Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (GameAction action in catalog.Actions.Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Title)))
                    titles[action.Id] = action.Title;
                foreach (SetupCommand command in game)
                {
                    string title;
                    if (titles.TryGetValue(command.Action ?? string.Empty, out title))
                        command.Title = title;
                }
            }
        }

        // engine setup grip-sweep [--games DCS,IL2Game,StarCitizen] [--only-aircraft F-16C_50] [--only <catalog ids>]
        // (review 7 B12 / L44) Every catalog stick or grip with a numbered trigger, planned alone: the gun of every game lands
        // on a trigger without a modifier. Exit code 1 names the grips where it does not. Runs on the catalog only (the
        // connected devices are left out: JOYPRO_FAKE_DEVICES points at an empty list for this run).
        private static int RunSetupGripSweep(List<string> args, string outPath)
        {
            List<string> games = SplitList(PopOptionValue(args, "--games"));
            if (games.Count == 0)
                games = new List<string> { "DCS", "IL2Game", "StarCitizen" };
            List<string> aircraft = SplitList(PopOptionValue(args, "--only-aircraft"));
            if (aircraft.Count == 0)
                aircraft = new List<string> { "F-16C_50" };
            List<string> only = SplitList(PopOptionValue(args, "--only"));
            if (args.Count > 0)
                return Fail("Unknown setup grip-sweep argument(s): " + string.Join(" ", args));
            string empty = Path.Combine(Path.GetTempPath(), "joypro-grip-sweep-" + Guid.NewGuid().ToString("N") + ".tsv");
            File.WriteAllText(empty, "# JoyPro device\tVID\tPID\tHID product string\taxes\tbuttons\thats" + Environment.NewLine, new UTF8Encoding(false));
            string before = Environment.GetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable);
            Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, empty);
            Regex trigger = new Regex(@"^\w+\.trigger(\.|$)", RegexOptions.Compiled);
            StringBuilder report = new StringBuilder();
            int grips = 0, failed = 0, skipped = 0;
            try
            {
                List<string> kbWarnings = new List<string>();
                foreach (KbDevice model in EngineDeviceKb.Load(kbWarnings).Where(k => k.Kind == "stick" || k.Kind == "grip" || k.Kind == "hotas" || k.IsGrip)
                             .Where(k => k.Controls.Any(c => trigger.IsMatch(c.Control ?? string.Empty))).OrderBy(k => k.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (only.Count > 0 && !only.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
                        continue;
                    List<string> warnings = new List<string>();
                    EngineSetupWizard.DeviceChoices choices = new EngineSetupWizard.DeviceChoices();
                    choices.Added.Add(model.Id);
                    List<SetupInput> inputs;
                    List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                    SetupDevice device = devices.FirstOrDefault(d => SetupRoles.Binds(d.Role));
                    if (device == null || !inputs.Any(i => i.Device == device.Device && !i.IsAxis && trigger.IsMatch(i.Control ?? string.Empty)))
                    {
                        skipped++;
                        report.AppendLine(model.Id + ": skipped (no numbered trigger input" + (device == null ? ", no role" : string.Empty) + ")");
                        continue;
                    }
                    grips++;
                    SetupOptions options = new SetupOptions { Games = games, OnlyAircraft = aircraft, Seed = 1 };
                    SetupRequest request = EngineSetupWizard.Request(devices, inputs, options, new List<string>(), warnings);
                    SetupPlan plan = SetupPlanner.Plan(request);
                    List<string> misses = new List<string>();
                    foreach (string game in games)
                    {
                        List<SetupPlanRelation> guns = plan.Placed.Where(r => (r.Function == "trigger1ststage" || r.Function == "trigger2ndstage") &&
                                                                              r.Nodes.Any(n => string.Equals(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game, game, StringComparison.OrdinalIgnoreCase))).ToList();
                        bool gunCommand = request.Commands.Any(c => string.Equals(c.Game, game, StringComparison.OrdinalIgnoreCase) && (c.Function == "trigger1ststage" || c.Function == "trigger2ndstage"));
                        if (!gunCommand)
                            continue;
                        if (!guns.Any(r => r.Modifier.Length == 0 && trigger.IsMatch(r.Control ?? string.Empty)))
                            misses.Add(game + ": gun " + (guns.Count == 0 ? "not placed" : "on " + string.Join(", ", guns.Select(r => r.Control + (r.Modifier.Length > 0 ? " + " + r.Modifier : string.Empty)).Distinct())));
                    }
                    if (misses.Count > 0)
                        failed++;
                    report.AppendLine(model.Id + ": " + (misses.Count == 0 ? "ok" : "FAIL " + string.Join("; ", misses)));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, before);
                try { File.Delete(empty); } catch (IOException) { }
            }
            report.Insert(0, "Grip sweep: " + grips.ToString(CultureInfo.InvariantCulture) + " grips with a trigger, gun on the trigger in " + (grips - failed).ToString(CultureInfo.InvariantCulture) +
                             ", failed " + failed.ToString(CultureInfo.InvariantCulture) + ", skipped " + skipped.ToString(CultureInfo.InvariantCulture) + " (games " + string.Join(", ", games) + ")" + Environment.NewLine);
            EmitPayload(report.ToString(), outPath);
            return failed > 0 ? 1 : 0;
        }

        private static int RunSetupGames(List<string> args, string outPath, bool textOutput)
        {
            List<string> games = SplitList(PopOptionValue(args, "--games"));
            Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in PopOptionValues(args, "--path"))
            {
                int split = item.IndexOf('=');
                if (split <= 0 || split == item.Length - 1)
                    return Fail("--path expects \"<game>=<config folder>\".");
                paths[item.Substring(0, split).Trim()] = NormalizePath(item.Substring(split + 1).Trim());
            }
            bool statusOnly = PopFlag(args, "--status-only");
            if (args.Count > 0)
                return Fail("Unknown setup games argument(s): " + string.Join(" ", args));
            GameDeviceDirectory.ReadOnceThisRun();
            if (games.Count == 0)
                games = new[] { "DCS" }.Concat(EngineSetupEval.WizardGames).ToList();
            HashSet<string> wizard = new HashSet<string>(new[] { "DCS" }.Concat(EngineSetupEval.WizardGames), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (string game in games)
            {
                string path;
                paths.TryGetValue(game, out path);
                EngineProfileGameState state;
                try
                {
                    state = EngineSync.GameState(game, path, statusOnly ? (Func<string, bool>)(id => ProfileBindsOf(id) > 0) : null);
                }
                catch (Exception ex)
                {
                    state = new EngineProfileGameState { Game = game };
                    state.Warnings.Add(ex.Message);
                }
                int profileBinds = ProfileBindsOf(state.Game.Length > 0 ? state.Game : game);
                string status = profileBinds > 0 || state.Imported || state.Exported ? "profile" : state.GameJoystickBindCount > 0 ? "game" : "empty";
                // The wizard plans every game with a function table of its own (what engine games support shows as
                // "Setup wizard / Auto Assign"), the pad games included.
                bool derivedTable;
                bool planned = wizard.Contains(game) || (EngineGameSupport.FunctionRows(game, out derivedTable) > 0 && !derivedTable);
                list.Add(new Dictionary<string, object>
                {
                    { "Game", state.Game.Length > 0 ? state.Game : game }, { "Status", status }, { "Wizard", planned }, { "ConfigFound", state.ConfigPath.Length > 0 },
                    { "GameBinds", state.GameJoystickBindCount }, { "GameKeyboardBinds", state.GameBindCount - state.GameJoystickBindCount }, { "GameBindsRead", state.GameFilesRead },
                    { "ProfileBinds", profileBinds }, { "Imported", state.Imported }, { "Exported", state.Exported },
                    { "Warnings", state.Warnings.Cast<object>().ToList() }
                });
            }
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Setup Games: " + list.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Dictionary<string, object> item in list)
                    sb.AppendLine("  - " + item["Game"] + " | " + item["Status"] + " | game binds " + ((bool)item["GameBindsRead"] ? item["GameBinds"].ToString() : "not read") + " | profile binds " + item["ProfileBinds"] +
                                  ((bool)item["Wizard"] ? string.Empty : " | no auto setup") + ((bool)item["ConfigFound"] ? string.Empty : " | no control files yet"));
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(MiniJson.Write(new Dictionary<string, object> { { "Games", list.Cast<object>().ToList() } }, false), outPath);
            }
            return 0;
        }

        // engine setup preview (--plan <plan.json> | --from-profile) [--game <id>] [--aircraft <key>] [--out-dir <folder>] [--light] [--width <px>] [--live-data]:
        // (--live-data: instead of finished sheets, per device its drawing, callout sheet and row texts for the layout
        // window, which draws them itself and lights up the pressed input)
        // the layout preview window (docs/setup-wizard.md#layout-preview): the games and aircraft that have binds, and with
        // --aircraft and --out-dir one sheet per device (JoyPro's drawing with the function names of that aircraft, as setup
        // render draws them). --from-profile reads the profile's binds on the connected devices instead of a plan.
        // Read-only (the sheets go to --out-dir).
        private static int RunSetupPreview(List<string> args, string outPath, bool textOutput)
        {
            string planFile = PopOptionValue(args, "--plan");
            bool fromProfile = PopFlag(args, "--from-profile");
            string game = PopOptionValue(args, "--game") ?? string.Empty;
            string aircraft = PopOptionValue(args, "--aircraft") ?? string.Empty;
            string outDir = PopOptionValue(args, "--out-dir");
            bool light = PopFlag(args, "--light");
            bool liveData = PopFlag(args, "--live-data");
            int width = PopOptionInt(args, "--width", 1600);
            DrawingStyle optionStyle = DrawingStyle.FromOptions(args, DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true));
            if (optionStyle != null)
                EngineCalloutRenderer.ExportStyle = optionStyle;
            if (args.Count > 0)
                return Fail("Unknown setup preview argument(s): " + string.Join(" ", args));
            if (fromProfile == !string.IsNullOrWhiteSpace(planFile))
                return Fail("setup preview needs --plan <plan.json> or --from-profile.");
            List<string> warnings = new List<string>();
            SetupPlan plan;
            if (fromProfile)
            {
                plan = EngineSetupWizard.ProfilePlan(warnings);
            }
            else
            {
                if (!File.Exists(NormalizePath(planFile)))
                    return Fail("The plan was not found: " + planFile);
                try
                {
                    plan = SetupPlanJson.Read(File.ReadAllText(NormalizePath(planFile), Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    return Fail("The plan could not be read: " + ex.Message);
                }
            }
            // Games and their aircraft with at least one placed function (DCS input layers are left out: they belong to
            // every aircraft).
            Dictionary<string, SetupAircraft> known;
            Dictionary<string, SetupFunction> functions;
            List<SetupCommand> commands;
            SetupCatalog.Load(EngineSetupWizard.FunctionsFolder(), new List<string>(), out functions, out known, out commands);
            Dictionary<string, SortedSet<string>> byGame = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupPlanRelation relation in plan.Placed)
            {
                IEnumerable<SetupNode> nodes = relation.Nodes.Count > 0 ? relation.Nodes : relation.Aircraft.Select(a => new SetupNode { Aircraft = a, Game = GameOfAircraft(known, a) });
                foreach (SetupNode node in nodes)
                {
                    string g = string.IsNullOrWhiteSpace(node.Game) ? GameOfAircraft(known, node.Aircraft) : (EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game);
                    SetupAircraft plane;
                    if (string.IsNullOrWhiteSpace(node.Aircraft) || (known.TryGetValue(node.Aircraft, out plane) && plane.SharedLayer))
                        continue;
                    if (game.Length > 0 && !string.Equals(g, game, StringComparison.OrdinalIgnoreCase))
                        continue;
                    SortedSet<string> set;
                    if (!byGame.TryGetValue(g, out set))
                        byGame[g] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(node.Aircraft);
                }
            }
            List<KeyValuePair<string, string>> sheets = new List<KeyValuePair<string, string>>();
            List<Dictionary<string, object>> live = new List<Dictionary<string, object>>();
            if (liveData && aircraft.Length > 0 && !string.IsNullOrWhiteSpace(outDir))
            {
                live = EngineSetupWizard.LiveData(plan, NormalizePath(outDir), aircraft, light, warnings);
            }
            else if (aircraft.Length > 0 && !string.IsNullOrWhiteSpace(outDir))
            {
                // The sheet's title names the aircraft's game: a plan made from the profile has no quality list.
                if (plan.Quality.All(q => !string.Equals(q.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase)))
                    plan.Quality.Add(new SetupQuality { Aircraft = aircraft, Game = game.Length > 0 ? game : GameOfAircraft(known, aircraft) });
                sheets = EngineSetupWizard.Render(plan, NormalizePath(outDir), aircraft, light, width, warnings);
            }
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Setup Preview: " + (fromProfile ? "your profile" : "the plan"));
                foreach (KeyValuePair<string, SortedSet<string>> item in byGame.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("  - " + item.Key + ": " + string.Join(", ", item.Value));
                foreach (KeyValuePair<string, string> sheet in sheets)
                    sb.AppendLine("Sheet: " + sheet.Key + " => " + sheet.Value);
                foreach (string warning in warnings.Distinct())
                    sb.AppendLine("Warning: " + warning);
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(MiniJson.Write(new Dictionary<string, object>
                {
                    { "Source", fromProfile ? "profile" : "plan" },
                    { "Games", byGame.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => (object)new Dictionary<string, object> { { "Game", g.Key }, { "Aircraft", g.Value.Cast<object>().ToList() } }).ToList() },
                    { "Sheets", sheets.Select(s =>
                        {
                            SetupDevice device = plan.Devices.FirstOrDefault(d => d.Device == s.Key);
                            return (object)new Dictionary<string, object>
                            {
                                { "Device", s.Key }, { "Name", device != null ? SetupRoles.Short(device) : s.Key }, { "Role", device != null ? device.Role : string.Empty }, { "File", s.Value }
                            };
                        }).ToList() },
                    { "Devices", live.Cast<object>().ToList() },
                    { "Warnings", warnings.Distinct().Cast<object>().ToList() }
                }, false), outPath);
            }
            return 0;
        }

        private static string GameOfAircraft(Dictionary<string, SetupAircraft> known, string aircraft)
        {
            SetupAircraft plane;
            return known.TryGetValue(aircraft ?? string.Empty, out plane) && plane.Game.Length > 0 ? plane.Game : aircraft ?? string.Empty;
        }

        // Relations of the profile with a bind (device and input) and a command of the game.
        private static int ProfileBindsOf(string game)
        {
            int count = 0;
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>())
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                bool ofGame = relation.NODES.Any(n => n != null && (EngineAutoGroup.IsDcs(game) ? EngineAutoGroup.IsDcs(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game) : string.Equals(n.Game, game, StringComparison.OrdinalIgnoreCase)));
                if (!ofGame)
                    continue;
                Bind bind;
                if (!InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                string input = relation.ISAXIS ? bind.JAxis : bind.JButton;
                if (!string.IsNullOrWhiteSpace(input))
                    count++;
            }
            return count;
        }

        private static string ParseSetupDeviceChoices(List<string> args, out EngineSetupWizard.DeviceChoices choices)
        {
            choices = new EngineSetupWizard.DeviceChoices();
            foreach (string grip in PopOptionValues(args, "--grip"))
            {
                int split = grip.LastIndexOf('=');
                if (split <= 0 || split == grip.Length - 1)
                    return "--grip expects \"<device or VID:PID>=<grip id>\".";
                choices.Grips[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
            }
            foreach (string role in PopOptionValues(args, "--role"))
            {
                int split = role.LastIndexOf('=');
                if (split <= 0 || split == role.Length - 1)
                    return "--role expects \"<device or VID:PID>=<role>\" (" + string.Join(", ", SetupRoles.All) + ").";
                choices.Roles[role.Substring(0, split).Trim()] = role.Substring(split + 1).Trim().ToLowerInvariant();
            }
            choices.Added.AddRange(PopOptionValues(args, "--add-device"));
            return null;
        }

        private static List<string> SplitList(string value)
        {
            return (value ?? string.Empty).Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> SplitTier(string value)
        {
            return (value ?? string.Empty).Split(new[] { '|', '=' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        }
    }
}
