using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // Auto Group and Fill all keep the auto setup's rules for a relation that is already bound to an input
    // (docs/auto-setup-eval.md#hard-gates): nothing but the lever's own functions on a lever detent (an engine only as the
    // one-way cut-off of its own lever), nothing on an axis zone button, only an on / off pair or what its label names on a
    // latched switch, nothing irreversible on a grip, and never both directions of a function (lights on and off) for one
    // aircraft on one input. A command that would break one stays out of that relation (Auto Group may still put it into
    // an unbound one).
    internal sealed class EngineBindGuard
    {
        private readonly Dictionary<string, SetupInput> inputs = new Dictionary<string, SetupInput>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> deviceOfProduct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Stick bases whose grip is not chosen: their buttons are (nearly all) grip buttons.
        private readonly HashSet<string> gripUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SetupFunction> functions;
        private readonly Dictionary<string, List<SetupCommand>> commands = new Dictionary<string, List<SetupCommand>>(StringComparer.OrdinalIgnoreCase);
        private readonly EngineFunctionMap.Map map;
        // game|plane -> the catalog's functions of that aircraft (one engine or two)
        private readonly Dictionary<string, HashSet<string>> planeFunctions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex StartWords = new Regex(@"start|toggle|on/off|start/stop|\bign", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private EngineBindGuard(Dictionary<string, SetupFunction> functions, EngineFunctionMap.Map map)
        {
            this.functions = functions ?? new Dictionary<string, SetupFunction>(StringComparer.OrdinalIgnoreCase);
            this.map = map;
        }

        // null when no relation is bound (nothing to guard) or the catalog cannot be read.
        public static EngineBindGuard Load(List<string> warnings, EngineFunctionMap.Map map)
        {
            if (!InternalDataManagement.AllBinds.Values.Any(b => b != null && !string.IsNullOrWhiteSpace(b.Joystick)))
                return null;
            Dictionary<string, SetupFunction> functions;
            Dictionary<string, SetupAircraft> aircraft;
            List<SetupCommand> all;
            List<string> errors = new List<string>();
            if (!SetupCatalog.Load(EngineSetupWizard.FunctionsFolder(), errors, out functions, out aircraft, out all))
                return null;
            EngineBindGuard guard = new EngineBindGuard(functions, map ?? EngineFunctionMap.Load(new List<string>()));
            // (plus the function map's actions the catalog rows do not list, as the auto setup and Auto Group read them)
            SetupRequest scratch = new SetupRequest { Functions = functions, Aircraft = aircraft, Commands = all.Where(c => !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase)).ToList() };
            try
            {
                EngineSetupWizard.AddMapCommands(scratch, new HashSet<string>(all.Select(c => EngineAutoGroup.IsDcs(c.Game) ? "DCS" : c.Game), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                warnings.Add("bind guard: the function map could not be read: " + ex.Message);
            }
            foreach (SetupCommand command in scratch.Commands)
            {
                string key = Key(command.Game, command.Action, command.Aircraft);
                List<SetupCommand> list;
                if (!guard.commands.TryGetValue(key, out list))
                    guard.commands[key] = list = new List<SetupCommand>();
                list.Add(command);
                string planeKey = Key(command.Game, string.Empty, command.Aircraft);
                HashSet<string> set;
                if (!guard.planeFunctions.TryGetValue(planeKey, out set))
                    guard.planeFunctions[planeKey] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(command.Function);
            }
            List<SetupInput> setupInputs;
            List<string> deviceWarnings = new List<string>();
            // (the bound devices JoyPro only knows from its catalog - added by hand, no USB id - by their catalog name)
            EngineSetupWizard.DeviceChoices choices = new EngineSetupWizard.DeviceChoices();
            List<KbDevice> catalog = EngineDeviceKb.Load(deviceWarnings);
            foreach (string joystick in InternalDataManagement.AllBinds.Values.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Joystick)).Select(b => b.Joystick).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string name = Regex.Replace(joystick, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty).Trim();
                KbDevice model = catalog.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                if (model != null && !choices.Added.Contains(model.Id))
                    choices.Added.Add(model.Id);
            }
            List<SetupDevice> devices = EngineSetupWizard.Devices(choices, deviceWarnings, out setupInputs);
            foreach (SetupDevice device in devices)
            {
                string product = EngineSetupWizard.Product(device.Device);
                if (!guard.deviceOfProduct.ContainsKey(product))
                    guard.deviceOfProduct[product] = device.Device;
                if (device.NeedsGrip)
                {
                    guard.gripUnknown.Add(device.Device);
                    guard.gripUnknown.Add(product);
                }
            }
            foreach (SetupInput input in setupInputs)
                guard.inputs[input.Device + "|" + (input.Input ?? string.Empty).Trim()] = input;
            return guard;
        }

        private static string Key(string game, string action, string plane)
        {
            string g = EngineRelationSuggest.GameKey(game);
            return g + "|" + (action ?? string.Empty) + "|" + (g == "DCS" ? (plane ?? string.Empty).Trim() : g);
        }

        // The catalog's functions of a command for one aircraft (else the function map's first function).
        private List<string> FunctionsOf(string game, string action, string plane, out bool startToo)
        {
            startToo = false;
            List<SetupCommand> list;
            if (commands.TryGetValue(Key(game, action, plane), out list) && list.Count > 0)
            {
                startToo = list.Any(c => Regex.IsMatch(c.Function ?? string.Empty, "start", RegexOptions.IgnoreCase) || StartWords.IsMatch(c.Control ?? string.Empty));
                return list.Select(c => c.Function).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            string g = EngineRelationSuggest.GameKey(game);
            EngineFunctionMap.Entry entry;
            if (map != null && map.ByKey.TryGetValue(g + "|" + EngineRelationSuggest.NodeId(g, action) + "|" + (g == "DCS" ? plane : g), out entry) && entry != null && entry.Functions.Count > 0)
            {
                EngineFunctionMap.Assignment first = entry.Functions[0];
                return first.Function.Slots.Where(l => l.Dir.Length == 0 || first.Dir.Length == 0 || string.Equals(l.Dir, first.Dir, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            return new List<string>();
        }

        private SetupInput InputOf(string relationName, bool isAxis)
        {
            Bind bind;
            if (string.IsNullOrEmpty(relationName) || !InternalDataManagement.AllBinds.TryGetValue(relationName, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                return null;
            string input = ((isAxis ? bind.JAxis : bind.JButton) ?? string.Empty).Trim();
            if (input.Length == 0)
                return null;
            SetupInput found;
            if (inputs.TryGetValue(bind.Joystick + "|" + input, out found))
                return found;
            string device;
            if (deviceOfProduct.TryGetValue(EngineSetupWizard.Product(bind.Joystick), out device) && inputs.TryGetValue(device + "|" + input, out found))
                return found;
            // A device added from the catalog (no USB id, named after its catalog entry): its catalog control.
            string name = Regex.Replace(bind.Joystick, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty).Trim();
            if (kb == null)
                kb = EngineDeviceKb.Load(new List<string>());
            KbDevice model = kb.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            KbControl control = model == null ? null : model.Controls.FirstOrDefault(c => string.Equals(c.Input, input, StringComparison.OrdinalIgnoreCase));
            if (control != null)
                return new SetupInput { Device = bind.Joystick, Input = input, Control = control.Control ?? string.Empty, Label = control.Label ?? string.Empty, Type = string.IsNullOrEmpty(control.Type) ? "momentary" : control.Type };
            return null;
        }

        private Dictionary<string, List<string>> onInput;
        private List<KbDevice> kb;

        private static string BindKey(Bind bind)
        {
            return bind.Joystick + "|" + (bind.JButton ?? string.Empty).Trim().ToUpperInvariant() + "|" +
                   string.Join(",", (bind.AllReformers ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        }

        // The other button relations bound to the same input with the same modifiers.
        private IEnumerable<string> RelationsOnInput(string name)
        {
            if (onInput == null)
            {
                onInput = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Bind> pair in InternalDataManagement.AllBinds)
                {
                    if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.Joystick) || string.IsNullOrWhiteSpace(pair.Value.JButton))
                        continue;
                    string key = BindKey(pair.Value);
                    List<string> list;
                    if (!onInput.TryGetValue(key, out list))
                        onInput[key] = list = new List<string>();
                    list.Add(pair.Key);
                }
            }
            Bind bind;
            List<string> same;
            if (!InternalDataManagement.AllBinds.TryGetValue(name, out bind) || bind == null || !onInput.TryGetValue(BindKey(bind), out same))
                return new List<string>();
            return same.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Active(RelationItem node, string game, string plane)
        {
            if (string.IsNullOrWhiteSpace(node.ID) || !string.Equals(EngineRelationSuggest.GameKey(node.Game), game, StringComparison.OrdinalIgnoreCase))
                return false;
            if (game != "DCS")
                return true;
            return node.AIRCRAFT != null && node.AIRCRAFT.Any(p => p.Value && string.Equals(p.Key.Trim(), (plane ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // Why the command must not go into the bound relation, or empty.
        public string Veto(Relation relation, string game, string action, string plane)
        {
            if (relation == null)
                return string.Empty;
            // (owner 2026-11-16: an axis relation keeps one rule - nothing that stays where it is set (a throttle, RPM,
            // mixture) joins a relation on a spring-return axis; Auto Group put the MSFS helicopter throttle on a left twist)
            if (relation.ISAXIS)
            {
                Bind axisBind;
                if (!InternalDataManagement.AllBinds.TryGetValue(relation.NAME ?? string.Empty, out axisBind) || axisBind == null || string.IsNullOrWhiteSpace(axisBind.Joystick) || string.IsNullOrWhiteSpace(axisBind.JAxis))
                    return string.Empty;
                SetupInput axis = InputOf(relation.NAME, true);
                if (axis == null)
                    return string.Empty;
                bool ignoredStart;
                foreach (string id in FunctionsOf(game, action, plane, out ignoredStart))
                {
                    string why = SetupPlanner.BoundInputVeto(null, id, axis, false, null, false);
                    if (why.Length > 0)
                        return why;
                }
                return string.Empty;
            }
            Bind bound;
            if (!InternalDataManagement.AllBinds.TryGetValue(relation.NAME ?? string.Empty, out bound) || bound == null || string.IsNullOrWhiteSpace(bound.Joystick) || string.IsNullOrWhiteSpace(bound.JButton))
                return string.Empty;
            // (the input's kind and label only when the catalog knows the device; the checks below need the bind alone)
            SetupInput input = InputOf(relation.NAME, relation.ISAXIS);
            // (a base whose grip is not chosen: a button is taken for a grip button - nothing irreversible goes there)
            if (input == null && (gripUnknown.Contains(bound.Joystick) || gripUnknown.Contains(EngineSetupWizard.Product(bound.Joystick))))
                input = new SetupInput { Device = bound.Joystick, Input = bound.JButton, Control = "stick.button.unknown", Type = "momentary" };
            bool startToo;
            List<string> mine = FunctionsOf(game, action, plane, out startToo);
            List<SetupCommand> own;
            bool toggleOrHold = SetupPlanner.ToggleOrHold(action, string.Empty) ||
                                (commands.TryGetValue(Key(game, action, plane), out own) && own.Any(c => SetupPlanner.ToggleOrHold(c.Action, c.Control)));
            // (one command filed under both engines, or an aircraft with one engine: its engine stop is the engine of any lever)
            HashSet<string> all;
            bool twoEngines = planeFunctions.TryGetValue(Key(game, string.Empty, plane), out all) && all.Any(f => f.StartsWith("left_engine_", StringComparison.OrdinalIgnoreCase)) &&
                              all.Any(f => f.StartsWith("right_engine_", StringComparison.OrdinalIgnoreCase));
            bool bothSides = !twoEngines || (mine.Any(f => f.StartsWith("left_", StringComparison.OrdinalIgnoreCase)) && mine.Any(f => f.StartsWith("right_", StringComparison.OrdinalIgnoreCase)));
            foreach (string id in mine)
            {
                SetupFunction function;
                functions.TryGetValue(id, out function);
                string sideless = bothSides ? Regex.Replace(id, "^(left|right)_", string.Empty, RegexOptions.IgnoreCase) : id;
                string why = input == null ? string.Empty : SetupPlanner.BoundInputVeto(function, sideless, input, startToo, functions.Values, toggleOrHold);
                if (why.Length > 0)
                    return why;
            }
            string g = EngineRelationSuggest.GameKey(game);
            // (B5 / L18: a one-way engine start / stop and another function of the aircraft never share one switch - the
            // switch's other positions; flipping it to "turn off" the other function would stop the engine)
            string sw = input == null ? string.Empty : SetupPlanner.SwitchOf(input.Control);
            if (sw.Length > 0)
            {
                bool engine = mine.Any(SetupPlanner.EngineOneWayFunction);
                foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
                {
                    if (pair.Value == null || pair.Value.ISAXIS || string.Equals(pair.Key, relation.NAME, StringComparison.OrdinalIgnoreCase))
                        continue;
                    SetupInput other = InputOf(pair.Key, false);
                    if (other == null || other.Device != input.Device || string.Equals(other.Input, input.Input, StringComparison.OrdinalIgnoreCase) || SetupPlanner.SwitchOf(other.Control) != sw)
                        continue;
                    foreach (RelationItem node in (pair.Value.NODES ?? new List<RelationItem>()).Where(n => n != null && Active(n, g, plane)))
                    {
                        bool ignored;
                        List<string> theirs = FunctionsOf(g, node.ID, plane, out ignored);
                        bool theirEngine = theirs.Any(SetupPlanner.EngineOneWayFunction);
                        if (theirs.Count > 0 && engine != theirEngine && (engine ? !theirs.Any(SetupPlanner.EngineFunction) : true))
                            return "an engine start / stop and another function would share one switch (" + pair.Key + " on its other position)";
                    }
                }
            }
            // Another relation on the same input (and modifiers) that already does something for the aircraft: one press
            // would run both (Auto Group's button check, here for Fill all too).
            foreach (string other in RelationsOnInput(relation.NAME))
            {
                Relation them;
                if (!InternalDataManagement.AllRelations.TryGetValue(other, out them) || them == null || them.ISAXIS)
                    continue;
                if ((them.NODES ?? new List<RelationItem>()).Any(n => n != null && Active(n, g, plane)))
                    return "its input already does '" + other + "' for " + (g == "DCS" ? plane : g) + " (one press would run both)";
            }
            // Both directions of a function for one aircraft on one input (lights on and off): one press would do both.
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID) || !string.Equals(EngineRelationSuggest.GameKey(node.Game), g, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(EngineRelationSuggest.NodeId(g, node.ID), EngineRelationSuggest.NodeId(g, action), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (g == "DCS" && (node.AIRCRAFT == null || !node.AIRCRAFT.Any(p => p.Value && string.Equals(p.Key.Trim(), (plane ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))))
                    continue;
                bool ignored;
                List<string> theirs = FunctionsOf(g, node.ID, plane, out ignored);
                foreach (string a in mine)
                    foreach (string b in theirs)
                        if (EngineSetupEval.Opposite(a, b))
                            return "'" + b + "' is already on that input: " + a + " would do the opposite with the same press";
            }
            return string.Empty;
        }
    }
}
