using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;
using JoyPro.Engine.VJoy;

namespace JoyPro.Engine
{
    // The virtual Xbox pad's auto setup (docs/virtual-joystick.md "Auto setup on the Xbox pad"): what the ACE COMBAT 8
    // live test of 2026-11-14 showed, as JoyPro's default for every profile. Deterministic, made again from the relations
    // each time (JoyPro's own functions carry the id prefix "auto-pad-" and go when nothing asks for them any more):
    //   1. Throttle: an axis on the accelerate trigger while the game decelerates on the other trigger becomes the
    //      centre-split triggers (forward RT, back LT); a brake axis then gets no trigger.
    //   2. Yaw on pad buttons (LB/RB): an "Axis ends to buttons" function (end zone 0.35) from the axis the relations put
    //      there, else the user's twist, else the rudder pedals.
    //   3. Chords (ACE COMBAT 8 flares = L3 + R3, unconfirmed): a macro pressing them together, from the user's flare /
    //      CMS forward relation.
    //   4. The D-pad of games that give menu commands on it (ACE COMBAT: wingmen): "Buttons to hat" from the user's comm
    //      switch or AI-menu hat, one not used for anything else of the game.
    //   5. One physical input feeds one thing: an input a function of the game reads leaves the table (a shift function
    //      excepted, it needs its slot).
    //   6. Look (the camera on a pad stick, IGamePadConventions.PadLookActions) with no axis of the profile on it: a free
    //      hat of the user's as "Hat to axes" (each direction holds the stick out while held), the stick's hat first (the
    //      owner, 2026-11-14: "on the topmost hat of my stick"). The hat stays the user's in every other game (trim in DCS).
    internal static partial class EngineVJoy
    {
        public const double PadYawEndZone = 0.35;

        private static string padSettingsJson;
        private static VJoySettings padSettings;

        // The invert a pad game's axis takes from "All games" (BindTuning.ResolveWithoutDetent): never DCS's base invert,
        // but the game's default for the relation's action (IGamePadConventions.PadInvertDefault), else not reversed. Null
        // for games not on the Xbox pad (the base applies as usual).
        internal static bool? PadBaseInvert(Bind bind, string game)
        {
            if (bind == null || string.IsNullOrWhiteSpace(game))
                return null;
            string json = InternalDataManagement.VirtualJoystickConfig ?? string.Empty;
            VJoySettings settings;
            lock (typeof(VJoySettings))
            {
                if (padSettings == null || !string.Equals(json, padSettingsJson, StringComparison.Ordinal))
                {
                    padSettings = LoadSettings();
                    padSettingsJson = json;
                }
                settings = padSettings;
            }
            if (!settings.IsXbox(game))
                return null;
            IGameIntegration integration;
            IGamePadConventions conventions = GameRegistry.TryResolve(game, out integration) ? integration as IGamePadConventions : null;
            Relation relation = bind.Rl ?? InternalDataManagement.AllRelations.Values.FirstOrDefault(r => r != null && ReferenceEquals(r.bind, bind));
            if (conventions != null && relation != null && relation.NODES != null)
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null && NodeMatches(game, n.Game)))
                {
                    bool? value = conventions.PadInvertDefault((node.ID ?? string.Empty).Trim());
                    if (value.HasValue)
                        return value.Value;
                }
            }
            return false;
        }

        private static readonly Regex PedalDevice = new Regex("pedal|rudder|crosswind|\\bTPR\\b|\\bTRP\\b|slaw|\\bMFG\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static List<string> PadAutoSetup(VJoySettings settings, string game, List<XboxUse> uses, Dictionary<string, string> layout)
        {
            List<string> notes = new List<string>();
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A game on the pad only by itself that the profile does not use gets nothing (its own functions go).
            if (settings.IsAutoXbox(game) && !InternalDataManagement.AllRelations.Values.Any(r => r != null && r.NODES != null && r.NODES.Any(n => n != null && NodeMatches(game, n.Game))))
            {
                functions.RemoveAll(f => f != null && (f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase));
                return notes;
            }
            Dictionary<string, string> titles = ActionTitles(game);

            notes.AddRange(ThrottleSplit(game, uses, layout, titles));
            notes.AddRange(AxisToPadButtons(settings, game, uses, layout, wanted));
            Lazy<RelationKnowledge> knowledge = new Lazy<RelationKnowledge>(() => RelationKnowledge.Build());
            notes.AddRange(YawFromTwistOrPedals(settings, game, uses, layout, titles, knowledge, wanted));
            IGameIntegration integration;
            IGamePadConventions chords = GameRegistry.TryResolve(game, out integration) ? integration as IGamePadConventions : null;
            if (chords != null)
            {
                foreach (PadChord chord in chords.PadChords ?? new PadChord[0])
                    notes.AddRange(ChordFromRelation(settings, game, chord, knowledge, wanted));
                foreach (PadChord hold in chords.PadHolds ?? new PadChord[0])
                    notes.AddRange(HoldFromRelation(settings, game, hold, wanted));
                if (chords.DPadFunctions != null && chords.DPadFunctions.Count > 0)
                    notes.AddRange(DPadFromMenuSwitch(settings, game, uses, chords.DPadFunctions, knowledge, wanted));
                // (review 6 M3: a stick without a throttle lever gets boost / brake on a free hat's up / down - before the
                // look-around takes a free hat: the throttle is a must-have, looking around is not)
                notes.AddRange(BoostBrakeOnFreeHat(settings, game, uses, layout, titles, chords, wanted));
                if (chords.PadLookActions != null && chords.PadLookActions.Count > 0)
                    notes.AddRange(LookOnFreeHat(settings, game, uses, layout, chords, wanted));
            }
            functions.RemoveAll(f => f != null && (f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) && !wanted.Contains(f.Id));
            notes.AddRange(OneUsePerInput(game, uses, functions));
            return notes;
        }

        // ---- the game's own actions

        private static Dictionary<string, string> ActionTitles(string game)
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IGameIntegration integration;
            if (!GameRegistry.TryResolve(game, out integration))
                return titles;
            foreach (GameAction action in GameCatalogStore.Load(integration, new List<string>()).Actions)
                titles[action.Id] = action.Title ?? string.Empty;
            return titles;
        }

        private static string ActionText(string action, Dictionary<string, string> titles)
        {
            string title;
            return action + " " + (titles.TryGetValue(action, out title) ? title : string.Empty);
        }

        private static readonly Regex Accelerate = new Regex("throttle.?up|accelerat|increase.?throttle|more.?power", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Decelerate = new Regex("throttle.?down|decelerat|decrease.?throttle|brake|reverse.?thrust", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex YawLeft = new Regex("yaw.?left|rudder.?left", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex YawRight = new Regex("yaw.?right|rudder.?right", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // 1. The throttle on the accelerate trigger while the decelerate action sits on the other trigger: both triggers,
        // split at the lever's centre. Another axis on the decelerate trigger (a toe brake relation) gives way.
        private static List<string> ThrottleSplit(string game, List<XboxUse> uses, Dictionary<string, string> layout, Dictionary<string, string> titles)
        {
            List<string> notes = new List<string>();
            KeyValuePair<string, string> up = layout.FirstOrDefault(a => XboxControls.IsTrigger(a.Value) && Accelerate.IsMatch(ActionText(a.Key, titles)));
            if (up.Key == null)
                return notes;
            string downControl = XboxControls.Triggers.First(t => !string.Equals(t, up.Value, StringComparison.OrdinalIgnoreCase));
            KeyValuePair<string, string> down = layout.FirstOrDefault(a => string.Equals(a.Value, downControl, StringComparison.OrdinalIgnoreCase) && Decelerate.IsMatch(ActionText(a.Key, titles)));
            if (down.Key == null || !string.Equals(up.Value, "XB_RT", StringComparison.OrdinalIgnoreCase))
                return notes;
            XboxUse throttle = uses.FirstOrDefault(u => IsAxisUse(u) && u.Actions.Contains(up.Key, StringComparer.OrdinalIgnoreCase));
            if (throttle == null)
                return notes;
            bool split = !string.Equals(throttle.Pinned, XboxControls.Triggers2, StringComparison.OrdinalIgnoreCase);
            throttle.Pinned = XboxControls.Triggers2;
            throttle.PinnedAction = up.Key + " + " + down.Key;
            foreach (XboxUse brake in uses.Where(u => u != throttle && IsAxisUse(u) && (u.Actions.Contains(down.Key, StringComparer.OrdinalIgnoreCase) ||
                                                                                       string.Equals(u.Pinned, downControl, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                uses.Remove(brake);
                notes.Add(string.Join(", ", brake.Relations) + " (" + brake.Source + " " + brake.Input + ") gets no trigger: the throttle " + string.Join(", ", throttle.Relations) +
                          " drives both triggers (forward " + XboxControls.Describe("XB_RT") + ", back " + XboxControls.Describe("XB_LT") + ").");
            }
            if (split && notes.Count == 0)
                notes.Add(string.Join(", ", throttle.Relations) + ": throttle on both triggers, split at the centre (forward " + XboxControls.Describe("XB_RT") + ", back " + XboxControls.Describe("XB_LT") + ").");
            return notes;
        }

        private static bool IsAxisUse(XboxUse use)
        {
            VJoyInput input;
            return VJoyInput.TryParseSlotToken(use.Input, out input) && input.Kind == VJoyInputKind.Axis;
        }

        // ---- the user's relations by what they do (the function map)

        internal sealed class RelationKnowledge
        {
            public sealed class Item
            {
                public Relation Relation;
                public Bind Bind;
                public List<string> Functions = new List<string>();
            }

            public List<Item> Items = new List<Item>();

            public static RelationKnowledge Build()
            {
                RelationKnowledge knowledge = new RelationKnowledge();
                EngineFunctionMap.Map map;
                try
                {
                    map = EngineFunctionMap.Load(null);
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    return knowledge;
                }
                foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(e => e.Value == null ? e.Key : e.Value.NAME ?? e.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Relation relation = entry.Value;
                    if (relation == null || relation.NODES == null)
                        continue;
                    Bind bind = relation.bind;
                    if (bind == null)
                        InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                    if (bind == null || !IsJoystickDevice(bind.Joystick) || IsVJoyName(bind.Joystick))
                        continue;
                    Item item = new Item { Relation = relation, Bind = bind };
                    foreach (RelationItem node in relation.NODES.Where(n => n != null && NodeActive(n)))
                    {
                        string nodeGame = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
                        List<string> planes = nodeGame == "DCS"
                            ? (node.AIRCRAFT == null ? new List<string>() : node.AIRCRAFT.Where(a => a.Value).Select(a => a.Key).ToList())
                            : new List<string> { nodeGame };
                        foreach (string plane in planes)
                        {
                            EngineFunctionMap.Entry found;
                            if (!map.ByKey.TryGetValue(nodeGame + "|" + (node.ID ?? string.Empty).Trim() + "|" + plane, out found))
                                continue;
                            foreach (EngineFunctionMap.Assignment a in found.Functions.Where(f => f.Weight >= 0.5))
                            {
                                string id = a.Function.Id + (a.Dir.Length > 0 ? ":" + a.Dir : string.Empty);
                                if (!item.Functions.Contains(id, StringComparer.OrdinalIgnoreCase))
                                    item.Functions.Add(id);
                                if (!item.Functions.Contains(a.Function.Id, StringComparer.OrdinalIgnoreCase))
                                    item.Functions.Add(a.Function.Id);
                            }
                            break;
                        }
                    }
                    if (item.Functions.Count > 0)
                        knowledge.Items.Add(item);
                }
                return knowledge;
            }

            public IEnumerable<Item> With(string function)
            {
                return Items.Where(i => i.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
            }
        }

        private static VirtualFunction AutoFunction(List<VirtualFunction> functions, string id, string type, string name, HashSet<string> wanted, out bool fresh)
        {
            wanted.Add(id);
            VirtualFunction function = functions.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            fresh = function == null || !string.Equals(function.Type, type, StringComparison.OrdinalIgnoreCase);
            if (fresh)
            {
                if (function != null)
                    functions.Remove(function);
                function = VirtualFunctionCatalog.Create(type, name);
                function.Id = id;
                function.Normalize();
                functions.Add(function);
            }
            return function;
        }

        private static string AutoId(string what, string source, string input)
        {
            return AutoAxisButtonsPrefix + what + "-" + Regex.Replace((source + "-" + input).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        }

        // Inputs the game already uses (its table's inputs and its functions' inputs), as "source|input".
        private static HashSet<string> UsedInputs(List<XboxUse> uses, IEnumerable<VirtualFunction> functions)
        {
            HashSet<string> used = new HashSet<string>(uses.Select(u => VJoyPlanner.Key(u.Source, u.Input)), StringComparer.OrdinalIgnoreCase);
            foreach (VirtualFunction f in functions.Where(f => f != null && f.Enabled))
            {
                foreach (VfInput input in f.Inputs.Where(i => i.Device.Length > 0 && i.Input.Length > 0))
                    used.Add(VJoyPlanner.Key(input.Device, input.Input));
            }
            return used;
        }

        // 2. Yaw on pad buttons with no axis on it: the user's twist (an axis relation of rudder / yaw on a stick), else
        // the rudder pedals.
        private static List<string> YawFromTwistOrPedals(VJoySettings settings, string game, List<XboxUse> uses, Dictionary<string, string> layout, Dictionary<string, string> titles,
                                                        Lazy<RelationKnowledge> knowledge, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            KeyValuePair<string, string> left = layout.FirstOrDefault(a => YawLeft.IsMatch(ActionText(a.Key, titles)) && !XboxControls.IsAxis(a.Value) && XboxControls.Fits(VJoyInputKind.Button, a.Value));
            KeyValuePair<string, string> right = layout.FirstOrDefault(a => YawRight.IsMatch(ActionText(a.Key, titles)) && !XboxControls.IsAxis(a.Value) && XboxControls.Fits(VJoyInputKind.Button, a.Value));
            if (left.Key == null || right.Key == null)
                return notes;
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            // Fed already: a relation of the game has the yaw actions (AxisToPadButtons, the table), or a function of the
            // user presses those buttons.
            if (uses.Any(u => u.Actions.Contains(left.Key, StringComparer.OrdinalIgnoreCase) || u.Actions.Contains(right.Key, StringComparer.OrdinalIgnoreCase)) ||
                functions.Any(f => f.Enabled && wanted.Contains(f.Id) && f.Type == VirtualFunctionCatalog.AxisButtons) ||
                functions.Any(f => f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                   f.Outputs.Any(o => string.Equals(o.Target, left.Value, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Target, right.Value, StringComparison.OrdinalIgnoreCase))))
                return notes;
            List<RelationKnowledge.Item> yaw = knowledge.Value.With("yaw").Where(i => i.Relation.ISAXIS && !string.IsNullOrWhiteSpace(i.Bind.JAxis)).ToList();
            RelationKnowledge.Item chosen = yaw.FirstOrDefault(i => !PedalDevice.IsMatch(i.Bind.Joystick)) ?? yaw.FirstOrDefault();
            if (chosen == null)
                return notes;
            VJoyInput input;
            int direction;
            if (!VJoyInput.TryFromBindToken(chosen.Bind.JAxis, out input, out direction) || input.Kind != VJoyInputKind.Axis)
                return notes;
            string source = chosen.Bind.Joystick.Trim();
            bool pedals = PedalDevice.IsMatch(source);
            bool fresh;
            VirtualFunction function = AutoFunction(functions, AutoId("yaw", source, input.ToSlotToken()), VirtualFunctionCatalog.AxisButtons,
                "Yaw on " + XboxControls.Describe(left.Value) + " / " + XboxControls.Describe(right.Value) + " from " + chosen.Relation.NAME, wanted, out fresh);
            SetIn(function, "in", source, input.ToSlotToken());
            SetOut(function, "low", left.Value);
            SetOut(function, "high", right.Value);
            function.Params["threshold"] = PadYawEndZone;
            if (fresh)
                notes.Add(GameName(game) + " yaws on " + XboxControls.Describe(left.Value) + " / " + XboxControls.Describe(right.Value) + ": JoyPro feeds them from " +
                          (pedals ? "the rudder pedals" : "the stick twist") + " (" + chosen.Relation.NAME + ", " + source + " " + input.ToSlotToken() + "), pressed past " +
                          ((int)Math.Round(PadYawEndZone * 100)).ToString(CultureInfo.InvariantCulture) + " % from the end.");
            return notes;
        }

        // 3. A chord (flares = L3 + R3 on ACE COMBAT 8): a macro pressing its controls together, from the user's relation of
        // the chord's function (flare, then CMS forward, then countermeasures).
        private static List<string> ChordFromRelation(VJoySettings settings, string game, PadChord chord, Lazy<RelationKnowledge> knowledge, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            RelationKnowledge.Item chosen = ChordNodeRelation(chord);
            foreach (string function in chosen != null ? new string[0] : chord.Functions ?? new string[0])
            {
                chosen = knowledge.Value.With(function).FirstOrDefault(i => !i.Relation.ISAXIS && !string.IsNullOrWhiteSpace(i.Bind.JButton) &&
                                                                           !(function == "cm_dispense" && i.Functions.Contains("cm_dispense:panic", StringComparer.OrdinalIgnoreCase)));
                if (chosen != null)
                    break;
            }
            if (chosen == null)
                return notes;
            string source = chosen.Bind.Joystick.Trim();
            string token = chosen.Bind.JButton.Trim();
            bool fresh;
            VirtualFunction macro = AutoFunction(functions, AutoId("chord", source, token), VirtualFunctionCatalog.Macro,
                chord.Name + " (" + string.Join(" + ", chord.Controls.Select(XboxControls.Describe)) + ") from " + chosen.Relation.NAME, wanted, out fresh);
            SetIn(macro, "trigger", source, token);
            SetCount(macro, chord.Controls.Length);
            macro.Steps = new List<VfStep>();
            for (int i = 0; i < chord.Controls.Length; i++)
            {
                string role = "out" + (i + 1).ToString(CultureInfo.InvariantCulture);
                SetOut(macro, role, chord.Controls[i]);
                macro.Steps.Add(new VfStep { Output = role, Action = "tap", HoldMs = chord.HoldMs > 0 ? chord.HoldMs : 120 });
            }
            macro.Params["together"] = 1;
            if (fresh)
                notes.Add(GameName(game) + ": " + chord.Name + " = " + string.Join(" + ", chord.Controls.Select(XboxControls.Describe)) + " together" + (chord.Confirmed ? string.Empty : " (not confirmed in game yet)") +
                          "; JoyPro presses them for " + chosen.Relation.NAME + " (" + source + " " + token + ").");
            return notes;
        }

        // The relation holding one of the chord's own actions (AceCombat8:Pad_Flares) with a button bind, by name.
        private static RelationKnowledge.Item ChordNodeRelation(PadChord chord)
        {
            HashSet<string> nodeIds = new HashSet<string>(chord.NodeIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (nodeIds.Count == 0)
                return null;
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.Where(e => e.Value != null && !e.Value.ISAXIS && e.Value.NODES != null)
                         .OrderBy(e => e.Value.NAME ?? e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!entry.Value.NODES.Any(n => n != null && NodeActive(n) && nodeIds.Contains((string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game.Trim()) + ":" + (n.ID ?? string.Empty).Trim())))
                    continue;
                Bind bind = entry.Value.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                if (bind == null || !IsJoystickDevice(bind.Joystick) || IsVJoyName(bind.Joystick) || string.IsNullOrWhiteSpace(bind.JButton))
                    continue;
                return new RelationKnowledge.Item { Relation = entry.Value, Bind = bind };
            }
            return null;
        }

        // A hold (ACE COMBAT High-G turn = both triggers full): a remap from the button of the user's relation named after it
        // ("High-G") to the hold's controls; they add to the throttle on the triggers (the larger value wins). Without
        // such a relation: offered in a note (and in pad-check).
        private static List<string> HoldFromRelation(VJoySettings settings, string game, PadChord hold, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            if (string.IsNullOrWhiteSpace(hold.NamePattern))
                return notes;
            Regex pattern = new Regex(hold.NamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            if (functions.Any(f => f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                   hold.Controls.All(c => f.Outputs.Any(o => string.Equals(o.Target, c, StringComparison.OrdinalIgnoreCase)))))
                return notes;
            Relation relation = null;
            Bind bind = null;
            // A relation named after the hold first ("High-G"), else one holding one of its actions (the Cobra button).
            HashSet<string> nodeIds = new HashSet<string>(hold.NodeIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<Relation, string, int> rank = (r, key) => pattern.IsMatch(r.NAME ?? key) ? 0
                : r.NODES != null && r.NODES.Any(n => n != null && NodeActive(n) && nodeIds.Contains((string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game.Trim()) + ":" + (n.ID ?? string.Empty).Trim())) ? 1 : 2;
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.Where(e => e.Value != null).OrderBy(e => rank(e.Value, e.Key)).ThenBy(e => e.Value.NAME ?? e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Value.ISAXIS || rank(entry.Value, entry.Key) > 1)
                    continue;
                Bind candidate = entry.Value.bind;
                if (candidate == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out candidate);
                if (candidate == null || !IsJoystickDevice(candidate.Joystick) || IsVJoyName(candidate.Joystick) || string.IsNullOrWhiteSpace(candidate.JButton))
                    continue;
                relation = entry.Value;
                bind = candidate;
                break;
            }
            if (relation == null)
            {
                // Plain words (the pad path's novice reads it): Set my devices automatically gives it a button.
                notes.Add(GameName(game) + ": " + hold.Name + " = " + string.Join(" + ", hold.Controls.Select(XboxControls.Describe)) + " fully pressed. Set my devices automatically puts it on a button " +
                          "(or call one of your functions \"" + hold.Name + "\"); the throttle on the triggers keeps working.");
                return notes;
            }
            string source = bind.Joystick.Trim();
            string token = bind.JButton.Trim();
            bool fresh;
            VirtualFunction remap = AutoFunction(functions, AutoId("hold", source, token), VirtualFunctionCatalog.Remap,
                hold.Name + " (" + string.Join(" + ", hold.Controls.Select(XboxControls.Describe)) + ") from " + relation.NAME, wanted, out fresh);
            SetIn(remap, "in", source, token);
            SetCount(remap, hold.Controls.Length);
            for (int i = 0; i < hold.Controls.Length; i++)
                SetOut(remap, "out" + (i + 1).ToString(CultureInfo.InvariantCulture), hold.Controls[i]);
            if (fresh)
                notes.Add(GameName(game) + ": " + hold.Name + " = " + string.Join(" + ", hold.Controls.Select(XboxControls.Describe)) + " fully pressed while " + relation.NAME +
                          " (" + source + " " + token + ") is held; the throttle on the triggers keeps working.");
            return notes;
        }

        // 4. The D-pad of a game that gives menu commands there: "Buttons to hat" from the user's comm switch or AI-menu
        // hat (relations of the game's D-pad functions, bound to one hat's directions or to buttons with a direction),
        // when the game's table leaves the D-pad free and the inputs serve nothing else of the game.
        private static List<string> DPadFromMenuSwitch(VJoySettings settings, string game, List<XboxUse> uses, IReadOnlyList<string> dpadFunctions, Lazy<RelationKnowledge> knowledge, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            if (uses.Any(u => XboxControls.Control(u.Pinned).StartsWith(XboxControls.DPad, StringComparison.OrdinalIgnoreCase)))
                return notes;
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            if (functions.Any(f => f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                   f.Outputs.Any(o => o.Target.StartsWith(XboxControls.DPad, StringComparison.OrdinalIgnoreCase))))
                return notes;
            HashSet<string> busy = UsedInputs(uses, functions.Where(f => !wanted.Contains(f.Id) && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "dpad", StringComparison.OrdinalIgnoreCase)));
            // A whole hat in the table makes its directions busy too.
            foreach (XboxUse use in uses)
            {
                VJoyInput hatInput;
                if (VJoyInput.TryParseSlotToken(use.Input, out hatInput) && hatInput.Kind == VJoyInputKind.Pov)
                {
                    for (int d = 0; d < 8; d++)
                        busy.Add(VJoyPlanner.Key(use.Source, hatInput.ToBindToken(d)));
                }
            }
            foreach (string function in dpadFunctions)
            {
                // Per source device: the four directions (up, right, down, left) from hat directions or buttons with a direction.
                foreach (IGrouping<string, RelationKnowledge.Item> device in knowledge.Value.With(function).Where(i => !i.Relation.ISAXIS && !string.IsNullOrWhiteSpace(i.Bind.JButton))
                             .GroupBy(i => i.Bind.Joystick.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    Dictionary<string, string> directions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (RelationKnowledge.Item item in device)
                    {
                        string token = item.Bind.JButton.Trim();
                        if (busy.Contains(VJoyPlanner.Key(device.Key, token)))
                            continue;
                        VJoyInput input;
                        int direction;
                        string role = null;
                        if (VJoyInput.TryFromBindToken(token, out input, out direction) && input.Kind == VJoyInputKind.Pov && direction >= 0 && direction % 2 == 0)
                            role = new[] { "up", "right", "down", "left" }[direction / 2];
                        else
                        {
                            string dir = item.Functions.Where(f => f.StartsWith(function + ":", StringComparison.OrdinalIgnoreCase)).Select(f => f.Substring(function.Length + 1)).FirstOrDefault() ?? string.Empty;
                            role = dir == "up" || dir == "fwd" ? "up" : dir == "down" || dir == "aft" ? "down" : dir == "left" ? "left" : dir == "right" ? "right" : null;
                        }
                        if (role != null && !directions.ContainsKey(role))
                            directions[role] = token;
                    }
                    if (directions.Count < 2)
                        continue;
                    bool fresh;
                    VirtualFunction hat = AutoFunction(functions, AutoId("dpad", device.Key, function), VirtualFunctionCatalog.ButtonsHat,
                        "D-pad (" + GameName(game) + " menu) from " + string.Join(", ", device.Select(i => i.Relation.NAME).Distinct()), wanted, out fresh);
                    foreach (string role in new[] { "up", "right", "down", "left" })
                        SetIn(hat, role, directions.ContainsKey(role) ? device.Key : string.Empty, directions.ContainsKey(role) ? directions[role] : string.Empty);
                    SetOut(hat, "hat", XboxControls.DPad);
                    if (fresh)
                        notes.Add(GameName(game) + " gives its menu commands on the D-pad: JoyPro feeds it from " + string.Join(", ", device.Select(i => i.Relation.NAME).Distinct()) +
                                  " (" + device.Key + ": " + string.Join(", ", directions.Select(d => d.Key + " " + d.Value)) + ").");
                    return notes;
                }
            }
            return notes;
        }

        private static readonly Regex LookRelation = new Regex("look|view|camera", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // 1b. Boost / brake on a free hat (review 6 M3): the game's throttle sits on the triggers (accelerate on RT, decelerate
        // on LT) but no axis of the profile drives them - a stick without a throttle lever. The up / down of a hat nothing of
        // the game uses press RT / LT fully while held (two "Remap" functions); the hat keeps its job in every other game.
        internal static List<string> BoostBrakeOnFreeHat(VJoySettings settings, string game, List<XboxUse> uses, Dictionary<string, string> layout, Dictionary<string, string> titles,
                                                          IGamePadConventions conventions, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            KeyValuePair<string, string> up = layout.FirstOrDefault(a => string.Equals(a.Value, "XB_RT", StringComparison.OrdinalIgnoreCase) && Accelerate.IsMatch(ActionText(a.Key, titles)));
            KeyValuePair<string, string> down = layout.FirstOrDefault(a => string.Equals(a.Value, "XB_LT", StringComparison.OrdinalIgnoreCase) && Decelerate.IsMatch(ActionText(a.Key, titles)));
            if (up.Key == null || down.Key == null)
                return notes;
            // Fed already: an axis splits the triggers, a relation of the game reaches the actions, or a function of the user
            // writes one trigger (the High-G hold presses both: it is no throttle).
            if (uses.Any(u => string.Equals(u.Pinned, XboxControls.Triggers2, StringComparison.OrdinalIgnoreCase) ||
                              u.Actions.Contains(up.Key, StringComparer.OrdinalIgnoreCase) || u.Actions.Contains(down.Key, StringComparer.OrdinalIgnoreCase)))
                return notes;
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            if (functions.Any(f => f != null && f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                   f.Outputs.Count(o => XboxControls.IsTrigger(o.Target ?? string.Empty)) == 1))
                return notes;
            HashSet<string> busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XboxUse use in uses)
            {
                int hat;
                if (HatOfToken(use.Input, out hat))
                    busy.Add(use.Source.Trim() + "|" + hat.ToString(CultureInfo.InvariantCulture));
            }
            foreach (VirtualFunction f in functions.Where(f => f != null && f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "boost-", StringComparison.OrdinalIgnoreCase) &&
                                                              !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "brake-", StringComparison.OrdinalIgnoreCase) &&
                                                              !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "look-", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (VfInput input in f.Inputs.Where(i => i.Device.Length > 0 && i.Input.Length > 0 && !VfChain.IsChain(i)))
                {
                    int hat;
                    if (HatOfToken(input.Input, out hat))
                        busy.Add(input.Device.Trim() + "|" + hat.ToString(CultureInfo.InvariantCulture));
                }
            }
            // The devices of the game's must-haves first (the stick and its grip), their lowest hat first.
            List<string> deviceOrder = new List<string>();
            foreach (string[] essential in conventions.PadEssentials ?? new string[0][])
            {
                if (essential == null || essential.Length < 2)
                    continue;
                foreach (XboxUse use in uses.Where(u => u.Actions.Contains(essential[1], StringComparer.OrdinalIgnoreCase)))
                {
                    if (!deviceOrder.Contains(use.Source.Trim(), StringComparer.OrdinalIgnoreCase))
                        deviceOrder.Add(use.Source.Trim());
                }
            }
            if (deviceOrder.Count == 0)
                return notes;
            string chosenDevice = null;
            int chosenHat = -1;
            foreach (PadDeviceInputs device in PadDevices().Where(d => deviceOrder.Contains(d.Device, StringComparer.OrdinalIgnoreCase))
                                                            .OrderBy(d => deviceOrder.FindIndex(o => string.Equals(o, d.Device, StringComparison.OrdinalIgnoreCase))))
            {
                foreach (int hat in device.Hats.Where(h => h < VJoySettings.MaxPovs).OrderBy(h => h))
                {
                    if (!busy.Contains(device.Device + "|" + hat.ToString(CultureInfo.InvariantCulture)))
                    {
                        chosenDevice = device.Device;
                        chosenHat = hat;
                        break;
                    }
                }
                if (chosenDevice != null)
                    break;
            }
            if (chosenDevice == null)
                return notes;
            string pov = "JOY_BTN_POV" + (chosenHat + 1).ToString(CultureInfo.InvariantCulture);
            bool freshUp, freshDown;
            VirtualFunction boost = AutoFunction(functions, AutoId("boost", chosenDevice, pov + "_U"), VirtualFunctionCatalog.Remap,
                "Throttle up (" + XboxControls.Describe("XB_RT") + " full) on " + pov + "_U of " + chosenDevice, wanted, out freshUp);
            SetIn(boost, "in", chosenDevice, pov + "_U");
            SetCount(boost, 1);
            SetOut(boost, "out1", "XB_RT");
            VirtualFunction brake = AutoFunction(functions, AutoId("brake", chosenDevice, pov + "_D"), VirtualFunctionCatalog.Remap,
                "Throttle down (" + XboxControls.Describe("XB_LT") + " full) on " + pov + "_D of " + chosenDevice, wanted, out freshDown);
            SetIn(brake, "in", chosenDevice, pov + "_D");
            SetCount(brake, 1);
            SetOut(brake, "out1", "XB_LT");
            if (freshUp || freshDown)
                notes.Add(GameName(game) + ": no lever drives the throttle, so JoyPro puts it on the hat " + (chosenHat + 1).ToString(CultureInfo.InvariantCulture) + " of " + chosenDevice +
                          ": up = more throttle (" + XboxControls.Describe("XB_RT") + " fully pressed), down = brake (" + XboxControls.Describe("XB_LT") + "); the hat keeps its job in your other games.");
            // (and the yaw on the same hat's left / right when nothing presses the game's yaw buttons - no pedals, no twist)
            KeyValuePair<string, string> left = layout.FirstOrDefault(a => YawLeft.IsMatch(ActionText(a.Key, titles)) && !XboxControls.IsAxis(a.Value) && XboxControls.Fits(VJoyInputKind.Button, a.Value));
            KeyValuePair<string, string> right = layout.FirstOrDefault(a => YawRight.IsMatch(ActionText(a.Key, titles)) && !XboxControls.IsAxis(a.Value) && XboxControls.Fits(VJoyInputKind.Button, a.Value));
            if (left.Key != null && right.Key != null &&
                !uses.Any(u => u.Actions.Contains(left.Key, StringComparer.OrdinalIgnoreCase) || u.Actions.Contains(right.Key, StringComparer.OrdinalIgnoreCase)) &&
                !functions.Any(f => f != null && f.Enabled && wanted.Contains(f.Id ?? string.Empty) && (f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "yaw-", StringComparison.OrdinalIgnoreCase)) &&
                !functions.Any(f => f != null && f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                    f.Outputs.Any(o => string.Equals(o.Target, left.Value, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Target, right.Value, StringComparison.OrdinalIgnoreCase))))
            {
                bool freshLeft, freshRight;
                VirtualFunction yawLeft = AutoFunction(functions, AutoId("hatyaw", chosenDevice, pov + "_L"), VirtualFunctionCatalog.Remap,
                    "Yaw left (" + XboxControls.Describe(left.Value) + ") on " + pov + "_L of " + chosenDevice, wanted, out freshLeft);
                SetIn(yawLeft, "in", chosenDevice, pov + "_L");
                SetCount(yawLeft, 1);
                SetOut(yawLeft, "out1", left.Value);
                VirtualFunction yawRight = AutoFunction(functions, AutoId("hatyaw", chosenDevice, pov + "_R"), VirtualFunctionCatalog.Remap,
                    "Yaw right (" + XboxControls.Describe(right.Value) + ") on " + pov + "_R of " + chosenDevice, wanted, out freshRight);
                SetIn(yawRight, "in", chosenDevice, pov + "_R");
                SetCount(yawRight, 1);
                SetOut(yawRight, "out1", right.Value);
                if (freshLeft || freshRight)
                    notes.Add(GameName(game) + ": the same hat's left / right yaw (" + XboxControls.Describe(left.Value) + " / " + XboxControls.Describe(right.Value) + "): nothing else presses them.");
            }
            return notes;
        }

        // 6. Look on a free hat: the game's look actions (PadLookActions, on its pad stick) with nothing of the profile on
        // them (no relation of the game reaches them, no function of the user writes that stick) get "Hat to axes" from a
        // hat nothing of the game uses. The hats are those the profile binds somewhere (any game, or as a modifier) and
        // those the connected devices have (PadDevices: the device catalog, else DirectInput). Best first: a hat the user looks around with in another game (a look / view / camera relation on
        // it), then the hats of the devices of the game's must-haves in their order (pitch, roll, gun ...: the stick and
        // its grip), then the rest by name; per device the lowest hat (POV1 is the top hat on most sticks). The function
        // runs only while this game's pad runs, so the hat keeps its job in every other game (trim in DCS).
        internal static List<string> LookOnFreeHat(VJoySettings settings, string game, List<XboxUse> uses, Dictionary<string, string> layout, IGamePadConventions conventions, HashSet<string> wanted)
        {
            List<string> notes = new List<string>();
            List<string> lookActions = (conventions.PadLookActions ?? new string[0]).Where(a => !string.IsNullOrWhiteSpace(a) && layout.ContainsKey(a)).ToList();
            string xControl = null;
            string yControl = null;
            foreach (string action in lookActions)
            {
                string control = XboxControls.Control(layout[action]).ToUpperInvariant();
                if (!XboxControls.Sticks.Contains(control))
                    continue;
                if (control.EndsWith("X", StringComparison.Ordinal))
                    xControl = xControl ?? control;
                else
                    yControl = yControl ?? control;
            }
            if (xControl == null && yControl == null)
                return notes;
            HashSet<string> controls = new HashSet<string>(new[] { xControl, yControl }.Where(c => c != null), StringComparer.OrdinalIgnoreCase);
            List<VirtualFunction> functions = settings.FunctionsOf(game);
            // Fed already: a relation of the game reaches a look action or sits on that stick, or a function of the user writes it.
            if (uses.Any(u => u.Actions.Any(a => lookActions.Contains(a, StringComparer.OrdinalIgnoreCase)) || controls.Contains(XboxControls.Control(u.Pinned ?? string.Empty))))
                return notes;
            if (functions.Any(f => f != null && f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase) &&
                                   f.Outputs.Any(o => controls.Contains(XboxControls.Control(o.Target ?? string.Empty)))))
                return notes;

            // The hats the game uses: its table's inputs and the inputs of its functions (JoyPro's own look function aside).
            HashSet<string> busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XboxUse use in uses)
            {
                int hat;
                if (HatOfToken(use.Input, out hat))
                    busy.Add(use.Source.Trim() + "|" + hat.ToString(CultureInfo.InvariantCulture));
            }
            foreach (VirtualFunction f in functions.Where(f => f != null && f.Enabled && !(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "look-", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (VfInput input in f.Inputs.Where(i => i.Device.Length > 0 && i.Input.Length > 0 && !VfChain.IsChain(i)))
                {
                    int hat;
                    if (HatOfToken(input.Input, out hat))
                        busy.Add(input.Device.Trim() + "|" + hat.ToString(CultureInfo.InvariantCulture));
                }
            }

            // The hats the profile knows (device|hat -> 0 a look relation is on it, 1 otherwise).
            Dictionary<string, int> known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, KeyValuePair<string, int>> parts = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, bool> know = (device, bindToken, look) =>
            {
                int hat;
                if (!IsJoystickDevice(device) || IsVJoyName(device) || !HatOfToken(bindToken, out hat) || hat >= VJoySettings.MaxPovs)
                    return;
                string key = device.Trim() + "|" + hat.ToString(CultureInfo.InvariantCulture);
                int rank = look ? 0 : 1;
                int was;
                known[key] = known.TryGetValue(key, out was) ? Math.Min(was, rank) : rank;
                parts[key] = new KeyValuePair<string, int>(device.Trim(), hat);
            };
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                if (bind == null)
                    continue;
                bool look = LookRelation.IsMatch(relation.NAME ?? entry.Key ?? string.Empty);
                know(bind.Joystick, relation.ISAXIS ? bind.JAxis : bind.JButton, look);
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier != null)
                        know(modifier.device, modifier.key, false);
                }
            }
            // The hats the connected devices really have (device catalog, else what DirectInput reports): a profile that
            // binds no hat yet (ACE COMBAT 8 alone) still finds the stick's free hat.
            foreach (PadDeviceInputs device in PadDevices())
            {
                foreach (int hat in device.Hats)
                    know(device.Device, "POV" + (hat + 1).ToString(CultureInfo.InvariantCulture), false);
            }

            // The devices of the game's must-haves, in their order (the stick first).
            List<string> deviceOrder = new List<string>();
            foreach (string[] essential in conventions.PadEssentials ?? new string[0][])
            {
                if (essential == null || essential.Length < 2)
                    continue;
                foreach (XboxUse use in uses.Where(u => u.Actions.Contains(essential[1], StringComparer.OrdinalIgnoreCase)))
                {
                    if (!deviceOrder.Contains(use.Source.Trim(), StringComparer.OrdinalIgnoreCase))
                        deviceOrder.Add(use.Source.Trim());
                }
            }
            Func<string, int> deviceRank = device =>
            {
                int index = deviceOrder.FindIndex(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            };
            string chosen = known.Keys.Where(k => !busy.Contains(k))
                .OrderBy(k => known[k]).ThenBy(k => deviceRank(parts[k].Key)).ThenBy(k => parts[k].Key, StringComparer.OrdinalIgnoreCase).ThenBy(k => parts[k].Value)
                .FirstOrDefault();
            if (chosen == null)
                return notes;
            string source = parts[chosen].Key;
            string token = "POV" + (parts[chosen].Value + 1).ToString(CultureInfo.InvariantCulture);
            string sticks = string.Join(" / ", new[] { xControl, yControl }.Where(c => c != null).Select(XboxControls.Describe));
            bool fresh;
            VirtualFunction function = AutoFunction(functions, AutoId("look", source, token), VirtualFunctionCatalog.HatAxes, "Look around (" + sticks + ") on " + token + " of " + source, wanted, out fresh);
            SetIn(function, "hat", source, token);
            SetOut(function, "x", xControl ?? string.Empty);
            SetOut(function, "y", yControl ?? string.Empty);
            if (fresh)
                notes.Add(GameName(game) + " looks around with " + sticks + ": no axis of the profile is on it, so JoyPro feeds it from the hat " + token + " of " + source +
                          " (each direction holds the stick out while held; the hat keeps its job in your other games).");
            return notes;
        }

        // A connected game controller's hats (0-based) and axes (JOY_X ...): its catalog layout (base and grip), else what
        // DirectInput reports. Read once per minute at most (the forwarder builds its pad plans again on every change).
        internal sealed class PadDeviceInputs
        {
            public string Device = string.Empty;
            public List<int> Hats = new List<int>();
            public List<string> Axes = new List<string>();
        }

        private static List<PadDeviceInputs> padDevices;
        private static DateTime padDevicesRead = DateTime.MinValue;
        private static string padDevicesFake;

        internal static List<PadDeviceInputs> PadDevices()
        {
            string fake = Environment.GetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable) ?? string.Empty;
            lock (typeof(PadDeviceInputs))
            {
                if (padDevices != null && string.Equals(fake, padDevicesFake, StringComparison.Ordinal) && (DateTime.UtcNow - padDevicesRead).TotalSeconds < 60)
                    return padDevices;
                List<PadDeviceInputs> list = new List<PadDeviceInputs>();
                try
                {
                    List<string> warnings = new List<string>();
                    GameDeviceDirectory directory = GameDeviceDirectory.Discover(warnings);
                    foreach (RosterDevice entry in EngineDeviceKb.BuildRoster(directory, null, null, null, warnings).Where(e => e.Connected && !IsVJoyName(e.JoyProName)))
                    {
                        PadDeviceInputs item = new PadDeviceInputs { Device = entry.JoyProName.Trim() };
                        List<KbControl> controls = new[] { entry.BaseModel, entry.Model }.Where(m => m != null && !entry.Generic).SelectMany(m => m.Controls.Concat(m.Axes)).ToList();
                        foreach (KbControl control in controls)
                        {
                            VJoyInput input;
                            int direction;
                            if (VJoyInput.TryFromBindToken(control.Input, out input, out direction) && input.Kind == VJoyInputKind.Pov && !item.Hats.Contains(input.Index))
                                item.Hats.Add(input.Index);
                            else if ((control.Input ?? string.Empty).StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) && !control.Input.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase) && !item.Axes.Contains(control.Input, StringComparer.OrdinalIgnoreCase))
                                item.Axes.Add(control.Input);
                        }
                        GameDevice device = directory.Devices.FirstOrDefault(d => string.Equals(d.JoyProName, entry.JoyProName, StringComparison.OrdinalIgnoreCase));
                        if (device != null && (item.Hats.Count == 0 || item.Axes.Count == 0))
                        {
                            directory.EnsureCapabilities(device, warnings);
                            if (device.CapabilitiesKnown && item.Hats.Count == 0)
                                item.Hats.AddRange(Enumerable.Range(0, Math.Max(0, Math.Min(VJoySettings.MaxPovs, device.PovCount))));
                            if (device.CapabilitiesKnown && item.Axes.Count == 0)
                                item.Axes.AddRange(device.AxisSlots.Select(GameInputTokens.AxisToken).Distinct(StringComparer.OrdinalIgnoreCase));
                        }
                        item.Hats.Sort();
                        list.Add(item);
                    }
                }
                catch (Exception)
                {
                    // No device list (no DirectInput): the profile's own hats only.
                }
                padDevices = list;
                padDevicesRead = DateTime.UtcNow;
                padDevicesFake = fake;
                return list;
            }
        }

        // The hat (0-based) of a whole-hat slot token (POV2) or a hat direction bind token (JOY_BTN_POV2_R).
        private static bool HatOfToken(string token, out int hat)
        {
            hat = -1;
            VJoyInput input;
            int direction;
            if (VJoyInput.TryParseSlotToken(token, out input) && input.Kind == VJoyInputKind.Pov)
            {
                hat = input.Index;
                return true;
            }
            if (VJoyInput.TryFromBindToken(token, out input, out direction) && input.Kind == VJoyInputKind.Pov)
            {
                hat = input.Index;
                return true;
            }
            return false;
        }

        private static void SetIn(VirtualFunction function, string role, string device, string input)
        {
            VfInput slot = function.InputOf(role);
            if (slot == null)
            {
                slot = new VfInput { Role = role };
                function.Inputs.Add(slot);
            }
            slot.Device = device ?? string.Empty;
            slot.Input = input ?? string.Empty;
        }

        private static void SetOut(VirtualFunction function, string role, string target)
        {
            VfOutput output = function.OutputOf(role);
            if (output == null)
            {
                output = new VfOutput { Role = role };
                function.Outputs.Add(output);
            }
            output.Target = target ?? string.Empty;
            output.VJoyDevice = 0;
        }

        // ---- engine vjoy pad-check

        // Per game on the pad, what the profile reaches there (read-only, the auto setup applied in memory): the throttle
        // split on both triggers, yaw, the chords (flares), the menu D-pad. A setup harness scores a profile with it.
        internal static int PadCheckCommand(List<string> args, bool textOutput, out string payload)
        {
            string gameRaw = PopValue(args, "--game");
            RefuseLeftovers(args, "pad-check");
            VJoySettings settings = LoadSettings();
            List<string> games;
            if (string.IsNullOrWhiteSpace(gameRaw))
                games = XboxGamesInUse(settings);
            else
            {
                string id = ResolveGame(gameRaw);
                if (id.Length == 0)
                    throw new ArgumentException("Unknown game: " + gameRaw);
                games = new List<string> { id };
            }
            List<string> warnings = new List<string>();
            List<XboxPlan> plans = BuildXboxPlans(settings, false, false, warnings);
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            foreach (string game in games)
            {
                XboxPlan plan = plans.FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase));
                List<Dictionary<string, object>> checks = new List<Dictionary<string, object>>();
                if (plan == null)
                {
                    items.Add(new Dictionary<string, object> { { "Game", game }, { "OnPad", false }, { "Checks", checks }, { "Score", 0.0 }, { "Notes", new List<string>() } });
                    continue;
                }
                Dictionary<string, string> layout = GamepadLayout(game, warnings);
                Dictionary<string, string> titles = ActionTitles(game);
                List<VirtualFunction> functions = settings.FunctionsOf(game).Where(f => f != null && f.Enabled).ToList();
                HashSet<string> fed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string> feed = control =>
                {
                    string c = XboxControls.Control(control ?? string.Empty).ToUpperInvariant();
                    if (c.Length == 0)
                        return;
                    fed.Add(c);
                    if (c == XboxControls.Triggers2)
                    {
                        fed.Add("XB_LT");
                        fed.Add("XB_RT");
                    }
                    if (c == XboxControls.DPad)
                    {
                        foreach (string d in new[] { "XB_DPAD_UP", "XB_DPAD_DOWN", "XB_DPAD_LEFT", "XB_DPAD_RIGHT" })
                            fed.Add(d);
                    }
                };
                foreach (VJoySlot slot in plan.Slots)
                    feed(slot.Target);
                foreach (VfOutput output in functions.SelectMany(f => f.Outputs).Where(o => o.Target.StartsWith("XB_", StringComparison.OrdinalIgnoreCase)))
                    feed(output.Target);

                IGameIntegration integration;
                IGamePadConventions chords = GameRegistry.TryResolve(game, out integration) ? integration as IGamePadConventions : null;
                // The must-have actions: the pad control the game has each on is fed.
                foreach (string[] essential in chords == null ? new string[0][] : (chords.PadEssentials ?? new string[0][]))
                {
                    string control;
                    if (essential == null || essential.Length < 2 || !layout.TryGetValue(essential[1], out control))
                        continue;
                    string what = ActionText(essential[1], titles).Substring(essential[1].Length).Trim();
                    what = what.Length == 0 ? essential[1] : what;
                    bool reached = fed.Contains(XboxControls.Control(control).ToUpperInvariant());
                    checks.Add(Check(essential[0], true, reached, what + " (" + XboxControls.Describe(control) + ")" + (reached ? " is fed." : ": no relation of the profile reaches it.")));
                }
                KeyValuePair<string, string> up = layout.FirstOrDefault(a => XboxControls.IsTrigger(a.Value) && Accelerate.IsMatch(ActionText(a.Key, titles)));
                bool throttleApplies = up.Key != null && layout.Any(a => XboxControls.IsTrigger(a.Value) && !string.Equals(a.Value, up.Value, StringComparison.OrdinalIgnoreCase) && Decelerate.IsMatch(ActionText(a.Key, titles)));
                bool split = plan.Slots.Any(s => string.Equals(XboxControls.Control(s.Target), XboxControls.Triggers2, StringComparison.OrdinalIgnoreCase));
                // (review 6 M3: without a throttle lever, boost / brake on a free hat - both triggers fully - is the throttle)
                bool hatThrottle = !split && functions.Any(f => f.Enabled && (f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "boost-", StringComparison.OrdinalIgnoreCase)) &&
                                   functions.Any(f => f.Enabled && (f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix + "brake-", StringComparison.OrdinalIgnoreCase));
                checks.Add(Check("throttle-split", throttleApplies, split || hatThrottle, split ? "The throttle drives both triggers (forward RT, back LT)."
                    : hatThrottle ? "No throttle lever: boost / brake on a hat press the triggers fully (RT / LT)." : "No axis splits the triggers."));

                KeyValuePair<string, string> left = layout.FirstOrDefault(a => YawLeft.IsMatch(ActionText(a.Key, titles)));
                KeyValuePair<string, string> right = layout.FirstOrDefault(a => YawRight.IsMatch(ActionText(a.Key, titles)));
                bool yawApplies = left.Key != null && right.Key != null;
                bool yaw = yawApplies && fed.Contains(XboxControls.Control(left.Value)) && fed.Contains(XboxControls.Control(right.Value));
                checks.Add(Check("yaw", yawApplies, yaw, yawApplies ? (yaw ? "Yaw " + XboxControls.Describe(left.Value) + " / " + XboxControls.Describe(right.Value) + " is fed." : "Nothing presses " + XboxControls.Describe(left.Value) + " / " + XboxControls.Describe(right.Value) + ".") : string.Empty));

                foreach (PadChord chord in chords == null ? new PadChord[0] : (chords.PadChords ?? new PadChord[0]))
                {
                    bool reached = functions.Any(f => f.Type == VirtualFunctionCatalog.Macro && chord.Controls.All(c => f.Outputs.Any(o => string.Equals(o.Target, c, StringComparison.OrdinalIgnoreCase))));
                    checks.Add(Check("chord:" + chord.Name.ToLowerInvariant(), true, reached, chord.Name + " (" + string.Join(" + ", chord.Controls.Select(XboxControls.Describe)) + (chord.Confirmed ? string.Empty : ", not confirmed in game") + ")" + (reached ? " is fed." : ": no relation feeds it.")));
                }
                foreach (PadChord hold in chords == null ? new PadChord[0] : (chords.PadHolds ?? new PadChord[0]))
                {
                    bool reached = functions.Any(f => f.Type != VirtualFunctionCatalog.Macro && hold.Controls.All(c => f.Outputs.Any(o => string.Equals(o.Target, c, StringComparison.OrdinalIgnoreCase))));
                    checks.Add(Check("hold:" + hold.Name.ToLowerInvariant(), true, reached, hold.Name + " (" + string.Join(" + ", hold.Controls.Select(XboxControls.Describe)) + " full)" + (reached ? " is fed." : ": no button holds it (Set my devices automatically puts it on one).")));
                }
                bool dpadApplies = chords != null && chords.DPadFunctions != null && chords.DPadFunctions.Count > 0;
                bool dpad = fed.Contains("XB_DPAD_UP") || fed.Contains("XB_DPAD_DOWN") || fed.Contains("XB_DPAD_LEFT") || fed.Contains("XB_DPAD_RIGHT");
                checks.Add(Check("menu-dpad", dpadApplies, dpad, dpadApplies ? (dpad ? "The D-pad (menu commands) is fed." : "Nothing feeds the D-pad.") : string.Empty));

                // Look around (the camera stick): an axis relation, or the auto setup's free hat (LookOnFreeHat).
                List<string> lookControls = (chords == null ? new string[0] : (chords.PadLookActions ?? new string[0]))
                    .Where(a => !string.IsNullOrWhiteSpace(a) && layout.ContainsKey(a)).Select(a => XboxControls.Control(layout[a]).ToUpperInvariant()).Distinct().ToList();
                List<string> lookFed = lookControls.Where(fed.Contains).ToList();
                string lookText = string.Join(" / ", lookControls.Select(XboxControls.Describe));
                // A hardware limit is no miss: every hat and axis of the connected devices holds a function of the game (a
                // single-hat HOTAS whose hat the target switch needs and no free axis). Said as n/a with its reason.
                string lookLimit = lookControls.Count > 0 && lookFed.Count == 0 ? LookHardwareLimit(plan, functions) : null;
                checks.Add(Check("look", lookControls.Count > 0 && lookLimit == null, lookFed.Count > 0, lookControls.Count == 0 ? string.Empty
                    : lookFed.Count == lookControls.Count ? "Look around (" + lookText + ") is fed."
                    : lookFed.Count > 0 ? "Look around is fed on " + string.Join(" / ", lookFed.Select(XboxControls.Describe)) + " only."
                    : lookLimit != null ? lookLimit
                    : "Nothing feeds look around (" + lookText + "): bind Look Horizontal / Look Vertical to an axis, or keep a hat free for it."));

                List<Dictionary<string, object>> applying = checks.Where(c => (bool)c["Applies"]).ToList();
                double score = applying.Count == 0 ? 1.0 : Math.Round(applying.Count(c => (bool)c["Ok"]) / (double)applying.Count, 3);
                items.Add(new Dictionary<string, object> { { "Game", game }, { "OnPad", true }, { "Checks", checks }, { "Score", score }, { "Notes", plan.Warnings } });
            }
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Games = items });
                return 0;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Dictionary<string, object> item in items)
            {
                sb.AppendLine(GameName((string)item["Game"]) + ((bool)item["OnPad"] ? ": score " + ((double)item["Score"]).ToString("0.###", CultureInfo.InvariantCulture) : ": not on the Xbox pad"));
                foreach (Dictionary<string, object> check in (List<Dictionary<string, object>>)item["Checks"])
                {
                    if ((bool)check["Applies"])
                        sb.AppendLine("  " + ((bool)check["Ok"] ? "ok  " : "MISS") + " " + check["Name"] + ": " + check["Detail"]);
                    else if (((string)check["Detail"]).StartsWith("n/a", StringComparison.Ordinal))
                        sb.AppendLine("  n/a  " + check["Name"] + ": " + check["Detail"]);
                }
            }
            payload = sb.ToString();
            return 0;
        }

        // Null when a connected device has a hat or an axis the game does not use (look around could go there), else the
        // reason in plain words (the hardware limit: documented in docs/virtual-joystick.md).
        private static string LookHardwareLimit(XboxPlan plan, List<VirtualFunction> functions)
        {
            List<PadDeviceInputs> devices = PadDevices();
            if (devices.Count == 0)
                return null;
            HashSet<string> busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> use = (device, token) =>
            {
                VJoyInput input;
                int direction;
                if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(token))
                    return;
                if (VJoyInput.TryParseSlotToken(token, out input) || VJoyInput.TryFromBindToken(token, out input, out direction))
                    busy.Add(device.Trim() + "|" + input.Kind + "|" + input.Index.ToString(CultureInfo.InvariantCulture));
            };
            foreach (VJoySlot slot in plan.Slots)
                use(slot.Source, slot.Input);
            foreach (VfInput input in functions.SelectMany(f => f.Inputs).Where(i => i.Device.Length > 0 && i.Input.Length > 0 && !VfChain.IsChain(i)))
                use(input.Device, input.Input);
            foreach (PadDeviceInputs device in devices)
            {
                if (device.Hats.Any(h => !busy.Contains(device.Device + "|" + VJoyInputKind.Pov + "|" + h.ToString(CultureInfo.InvariantCulture))))
                    return null;
                foreach (string axis in device.Axes)
                {
                    VJoyInput input;
                    int direction;
                    if (VJoyInput.TryFromBindToken(axis, out input, out direction) && !busy.Contains(device.Device + "|" + input.Kind + "|" + input.Index.ToString(CultureInfo.InvariantCulture)))
                        return null;
                }
            }
            return "n/a (hardware limit): every hat and axis of " + string.Join(", ", devices.Select(d => d.Device)) +
                   " holds a function of the game (the target switch and flares come first on a single-hat stick).";
        }

        private static Dictionary<string, object> Check(string name, bool applies, bool ok, string detail)
        {
            return new Dictionary<string, object> { { "Name", name }, { "Applies", applies }, { "Ok", applies && ok }, { "Detail", detail ?? string.Empty } };
        }

        // 5. One input, one job: a table input that a function of the game reads leaves the table (the function decides
        // what it does). A shift function keeps its slot: it needs the input's own control.
        private static List<string> OneUsePerInput(string game, List<XboxUse> uses, List<VirtualFunction> functions)
        {
            List<string> notes = new List<string>();
            Dictionary<string, VirtualFunction> read = new Dictionary<string, VirtualFunction>(StringComparer.OrdinalIgnoreCase);
            foreach (VirtualFunction f in functions.Where(f => f != null && f.Enabled && f.Type != VirtualFunctionCatalog.Shift))
            {
                foreach (VfInput input in f.Inputs.Where(i => i.Device.Length > 0 && i.Input.Length > 0 && !VfChain.IsChain(i)))
                {
                    string key = VJoyPlanner.Key(input.Device, input.Input);
                    if (!read.ContainsKey(key))
                        read[key] = f;
                }
            }
            foreach (XboxUse use in uses.ToList())
            {
                VirtualFunction f;
                if (!read.TryGetValue(VJoyPlanner.Key(use.Source, use.Input), out f))
                    continue;
                uses.Remove(use);
                if (!(f.Id ?? string.Empty).StartsWith(AutoAxisButtonsPrefix, StringComparison.OrdinalIgnoreCase))
                    notes.Add(use.Source + " " + use.Input + " (" + string.Join(", ", use.Relations) + ") gets no pad control of its own: the function \"" + f.Name + "\" reads it.");
            }
            return notes;
        }
    }
}
