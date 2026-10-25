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
                list.Add(new SetupInput { Device = entry.JoyProName, Input = axis.Input, Control = axis.Control, IsAxis = true, Type = "analog" });
            }
            foreach (KbControl control in controls.Where(c => c.Input.Length > 0 && !Diagonal.IsMatch(c.Control)))
            {
                if (list.Any(i => i.Input == control.Input))
                    continue;
                list.Add(new SetupInput { Device = entry.JoyProName, Input = control.Input, Control = control.Control, Label = control.Label ?? string.Empty, Type = control.Type, IsAxis = false });
            }
            string kind = entry.Model != null ? entry.Model.Kind : "stick";
            string family = kind == "stick" || kind == "throttle" || kind == "collective" || kind == "yoke" || kind == "base" ? (kind == "base" ? "stick" : kind)
                : kind == "hotas" ? "stick" : kind == "pedals" ? "pedals" : "panel";
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
            return list;
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

        public static SetupRequest Request(List<SetupDevice> devices, List<SetupInput> inputs, SetupOptions options, List<string> cockpitFiles, List<string> warnings)
        {
            SetupRequest request = new SetupRequest { Devices = devices, Inputs = inputs, Options = options };
            request.Warnings.AddRange(warnings);
            foreach (string game in options.Games ?? new List<string>())
                request.ModifierSupport[game] = EngineGameSupport.ModifierSupport(game);
            Dictionary<string, SetupFunction> functions;
            Dictionary<string, SetupAircraft> aircraft;
            List<SetupCommand> commands;
            List<string> errors = new List<string>();
            if (!SetupCatalog.Load(FunctionsFolder(), errors, out functions, out aircraft, out commands))
            {
                request.Warnings.AddRange(errors);
                return request;
            }
            request.Warnings.AddRange(errors);
            request.Functions = functions;
            request.Aircraft = aircraft;

            // Only commands the local database knows, with the type of their function (like Auto Group).
            HashSet<string> games = new HashSet<string>(options.Games, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> dbIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool?> axisCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (Lay out with AI with the profile's relations only needs none of them: no new relations are made.)
            foreach (SetupCommand command in options.ExistingOnly ? new List<SetupCommand>() : commands)
            {
                SetupFunction function;
                if (!games.Contains(command.Game) || !functions.TryGetValue(command.Function, out function) || !aircraft.ContainsKey(command.Aircraft))
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
                if (!isAxis.HasValue || isAxis.Value != function.IsAxis)
                    continue;
                request.Commands.Add(command);
            }
            if (!options.ExistingOnly)
                AddMapCommands(request, games);

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

            // The cockpit database: files the caller passes (engine cockpits get ... --json) and DB\Cockpits\*.json.
            CockpitPositionTable cockpits = new CockpitPositionTable();
            foreach (string file in cockpitFiles ?? new List<string>())
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
            CockpitCatalog cockpitDb = Program.LoadCockpitCatalog();
            cockpits.AddCatalog(cockpitDb, "DB\\Cockpits");
            request.Cockpits = cockpits;
            if (missing.Count > 0)
                request.Warnings.Add("Not in the local DCS database (refresh it to include them): " + string.Join(", ", missing.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).Take(12)) + (missing.Count > 12 ? " ..." : string.Empty));
            // New or unused aircraft follow the layout of the user's most used aircraft of their class (usage ranking).
            Program.AddUsageStyleRules(request);
            return request;
        }

        // The function knowledge map (EngineFunctionMap, as Auto Group uses it): the actions, aircraft and games the
        // catalog rows do not list join the slot the catalog uses most for their function (War Thunder, MSFS, X-Plane ...).
        private static void AddMapCommands(SetupRequest request, HashSet<string> games)
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
                        meaningOut.Add(sameId.Key + "|" + plane);
                    foreach (string note in notes)
                        result.Skipped.Add(planned.Name + ": " + note + " (left out, switched off)");
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
                        Game = string.IsNullOrEmpty(aircraft) ? "Setup plan" : GameOf(plan, aircraft),
                        Plane = string.IsNullOrEmpty(aircraft) ? "All aircraft" : aircraft,
                        Device = SetupRoles.Short(device) + " - " + SetupRoles.Title(device.Role)
                    };
                    EngineCalloutRenderer.ApplyStyle(options, set);
                    string file = Path.Combine(outDir, index.ToString("D2", CultureInfo.InvariantCulture) + "-" + Safe(SetupRoles.Short(device)) + (string.IsNullOrEmpty(aircraft) ? string.Empty : "-" + Safe(aircraft)) + ".png");
                    using (Bitmap rendered = EngineCalloutRenderer.Render(sheet, picture, text, options))
                        rendered.Save(file, ImageFormat.Png);
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

        private static string GameOf(SetupPlan plan, string aircraft)
        {
            SetupQuality quality = plan.Quality.FirstOrDefault(q => string.Equals(q.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase));
            return quality != null ? quality.Game : string.Empty;
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
        //     [--realism 0..1] [--seed N] [--replace-existing] [--role ...] [--grip ...] [--add-device ...] [--cockpit-file <json>]... [--out <plan.json>]
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
                            { "Aircraft", aircraft.Select(a => (object)new Dictionary<string, object>
                                {
                                    { "Key", a.Key }, { "Game", a.Game }, { "Type", a.Type }, { "Class", a.Class }, { "ClassTitle", SetupClasses.Title(a.Class) }, { "Era", a.Era }
                                }).ToList() },
                            { "Warnings", warnings.Distinct().Cast<object>().ToList() }
                        };
                        EmitPayload(MiniJson.Write(root, false), outPath);
                    }
                    return 0;
                }
                case "plan":
                {
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
                    string realism = PopOptionValue(args, "--realism");
                    if (realism != null)
                    {
                        double parsed;
                        if (!double.TryParse(realism.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > 100)
                            return Fail("--realism takes 0..1 (or 0..100 %).");
                        options.Realism = parsed > 1 || realism.EndsWith("%", StringComparison.Ordinal) ? parsed / 100.0 : parsed;
                    }
                    options.Seed = PopOptionInt(args, "--seed", 1);
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
                    return 0;
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
                default:
                    return Fail("Unknown setup action: " + action + " (devices, aircraft, plan, apply, render).");
            }
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
