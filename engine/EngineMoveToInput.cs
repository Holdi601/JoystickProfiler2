using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // relations item move-to-input: one action (of one game, or some aircraft of it) goes to another button or axis,
    // the rest of the relation stays where it is (tester question on 2.4.1: "I simply want to change the 'Toggle Master
    // Warning' from one button to another"). In JoyPro a relation is one input, so the action joins the relation that
    // already uses the new input (the rule of the DCS import's "add to my profile" and the take-over,
    // EngineDcsImport.RelationToJoin), else it gets a relation of its own named after it. One command = one undo step;
    // --dry-run only tells where it would go.
    internal static partial class Program
    {
        private sealed class MoveToInputResult
        {
            public string Action { get; set; }
            public string Game { get; set; }
            public List<string> Planes { get; set; }
            public string From { get; set; }
            public string To { get; set; }
            public bool Joined { get; set; }
            public bool Created { get; set; }
            public bool FromRemoved { get; set; }
            public string Device { get; set; }
            public string Input { get; set; }
            public List<string> Modifiers { get; set; }
            public bool DryRun { get; set; }
            // The input is taken for these aircraft: the relation there holds another action of the game for them (in a
            // relation an aircraft has one action), so the action cannot simply join it (tester report on 2.4.2).
            public bool Taken { get; set; }
            public string TakenRelation { get; set; }
            public List<string> TakenActions { get; set; }
            public List<string> TakenPlanes { get; set; }
            // ask (nothing changes, the caller picks), swap, replace or keep-both.
            public string IfTaken { get; set; }
            // swap / replace: the actions that left the taken relation, and where they went (swap: the source; replace: none).
            public List<string> Displaced { get; set; }
            public string DisplacedTo { get; set; }
            // keep-both: two relations on the same input, both fire when it is pressed.
            public bool BothFire { get; set; }
            public string Message { get; set; }
        }

        private static int MoveRelationItemToInput(string relationQuery, List<string> options, string outPath, bool textOutput)
        {
            List<string> nodeSpecs = PopOptionValues(options, "--node");
            string device = (PopOptionValue(options, "--device") ?? string.Empty).Trim();
            string input = (PopOptionValue(options, "--input") ?? string.Empty).Trim();
            List<string> modifierOptions = PopOptionValues(options, "--modifier");
            bool keepModifiers = PopFlag(options, "--keep-modifiers");
            string newName = (PopOptionValue(options, "--name") ?? string.Empty).Trim();
            bool dryRun = PopFlag(options, "--dry-run");
            string ifTaken = (PopOptionValue(options, "--if-taken") ?? "ask").Trim().ToLowerInvariant();
            if (options.Count > 0)
                return Fail("Unknown relations item move-to-input argument(s): " + string.Join(" ", options));
            if (nodeSpecs.Count != 1)
                return Fail("relations item move-to-input needs exactly one --node <game:id[:plane1|plane2]>.");
            if (device.Length == 0 || input.Length == 0)
                return Fail("relations item move-to-input needs --device <device> and --input <button or axis>.");

            if (ifTaken != "ask" && ifTaken != "swap" && ifTaken != "replace" && ifTaken != "keep-both")
                return Fail("--if-taken takes ask, swap, replace or keep-both.");

            NodeSpec spec;
            if (!TryParseNodeSpec(nodeSpecs[0], out spec))
                return Fail("Invalid --node value '" + nodeSpecs[0] + "'. Use --node <game:id[:plane1|plane2]>.");

            List<string> relationCandidates;
            string fromName;
            if (!TryResolveRelationName(relationQuery, out fromName, out relationCandidates))
                return FailResolution("relation", relationQuery, relationCandidates);
            Relation from = InternalDataManagement.AllRelations[fromName];
            RelationItem node = FindMatchingRelationItems(from, spec).FirstOrDefault();
            if (node == null)
                return Fail("Relation '" + fromName + "' does not hold " + spec.Game + ":" + spec.Id + ".");

            string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
            bool dcs = string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
            // DCS: the aircraft named in --node (all the node's switched-on aircraft without); other games: the whole node.
            List<string> active = node.GetActiveAircraftList();
            List<string> planes = dcs
                ? ((spec.Planes ?? new List<string>()).Count > 0 ? spec.Planes.Where(p => active.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList() : active)
                : new List<string> { game };
            if (dcs && planes.Count == 0)
                return Fail("The action is switched on for none of the given aircraft in '" + fromName + "'.");

            Bind fromBind = from.bind;
            if (fromBind == null)
                InternalDataManagement.AllBinds.TryGetValue(fromName, out fromBind);
            // A pressed button comes with the device as this PC names it ("11F0" or "11f0" in the GUID): the profile's
            // spelling of the same device, so a new relation is on the device the others are on.
            device = ProfileDeviceSpelling(device);
            List<string> modifiers = keepModifiers && fromBind != null && fromBind.AllReformers != null
                ? fromBind.AllReformers.Where(r => !string.IsNullOrWhiteSpace(r)).ToList()
                : NormalizeModifierInputs(modifierOptions);
            string fromInput = fromBind == null ? string.Empty : (from.ISAXIS ? fromBind.JAxis : fromBind.JButton);
            if (fromBind != null && !string.IsNullOrWhiteSpace(fromBind.Joystick) && !string.IsNullOrWhiteSpace(fromInput) &&
                string.Equals(Games.GameProfileBridge.BuildSignature(fromBind.Joystick, from.ISAXIS, fromInput, fromBind.AllReformers),
                    Games.GameProfileBridge.BuildSignature(device, from.ISAXIS, input, modifiers), StringComparison.OrdinalIgnoreCase))
                return Fail("'" + fromName + "' is on " + device + " " + input + " already.");

            string title = MoveActionTitle(node, game, planes);
            string nodeId = node.ID.Trim();
            string join = EngineDcsImport.RelationToJoin(InternalDataManagement.AllRelations, InternalDataManagement.AllBinds, null, device, from.ISAXIS, input, modifiers,
                game, planes, id => string.Equals(id, nodeId, StringComparison.OrdinalIgnoreCase), title, fromName);

            // No relation to join although one uses the input: it holds another action of the game for these aircraft.
            // Never "no relation uses that button" then: the caller is told and picks swap, replace or keep both.
            TakenInput taken = join == null ? FindTakenInput(fromName, device, from.ISAXIS, input, modifiers, game, dcs, planes, nodeId) : null;
            if (taken != null && (ifTaken == "swap" || ifTaken == "replace"))
                join = taken.Relation;

            // Whether the source keeps any action afterwards (a relation left empty goes away with its bind, as after a take-over).
            bool leavesEmpty = (from.NODES ?? new List<RelationItem>()).All(n => n == null || ReferenceEquals(n, node)) &&
                               (!dcs || planes.Count >= active.Count) &&
                               !(taken != null && ifTaken == "swap");
            string targetName = join ?? (newName.Length > 0 ? newName : title);
            if (join == null)
            {
                string root = targetName;
                int index = 2;
                while (InternalDataManagement.AllRelations.ContainsKey(targetName))
                    targetName = root + " (" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            }

            MoveToInputResult result = new MoveToInputResult
            {
                Action = title,
                Game = game,
                Planes = dcs ? planes : new List<string>(),
                From = fromName,
                To = targetName,
                Joined = join != null,
                Created = join == null,
                FromRemoved = leavesEmpty,
                Device = device,
                Input = input,
                Modifiers = modifiers,
                DryRun = dryRun,
                Taken = taken != null,
                TakenRelation = taken == null ? string.Empty : taken.Relation,
                TakenActions = taken == null ? new List<string>() : taken.Titles,
                TakenPlanes = taken == null ? new List<string>() : taken.Planes,
                IfTaken = ifTaken,
                Displaced = new List<string>(),
                DisplacedTo = string.Empty,
                BothFire = taken != null && ifTaken == "keep-both"
            };
            string where = device + " " + input + (modifiers.Count > 0 ? " (+" + string.Join(", ", modifiers.Select(ModifierLabelOf)) + ")" : string.Empty);

            if (taken != null && ifTaken == "ask")
            {
                // Nothing changes: the caller (the GUI's Move panel) shows the choices.
                result.To = taken.Relation;
                result.Joined = false;
                result.Created = false;
                result.FromRemoved = false;
                result.Message = "'" + taken.Relation + "' on " + where + " already has " + string.Join(", ", taken.Titles.Select(t => "'" + t + "'")) +
                                 (dcs ? " for " + string.Join(", ", taken.Planes) : string.Empty) + "; in a relation an aircraft has one action. " +
                                 "Pick --if-taken swap (that action goes to '" + fromName + "'), replace (that action leaves the input) or keep-both " +
                                 "(a relation of its own on the same input; both fire together).";
                if (!dryRun)
                    return Fail(result.Message);
                CommandChangedNothing = true;
                return EmitMoveResult(result, outPath, textOutput);
            }
            if (taken != null && ifTaken != "keep-both")
            {
                result.To = taken.Relation;
                result.Joined = true;
                result.Created = false;
                result.Displaced = taken.Titles;
                result.DisplacedTo = ifTaken == "swap" ? fromName : string.Empty;
            }

            if (!dryRun)
            {
                Relation target;
                if (join != null)
                    target = InternalDataManagement.AllRelations[join];
                else
                {
                    target = new Relation { NAME = targetName, ISAXIS = from.ISAXIS, Groups = new List<string>(from.Groups ?? new List<string>()), NODES = new List<RelationItem>() };
                    // The source's bind settings (an axis keeps its tuning), on the new input.
                    Bind bind = fromBind != null ? fromBind.Copy(target) : new Bind(target);
                    bind.Joystick = device;
                    bind.JoyBackup = device;
                    if (target.ISAXIS)
                    {
                        bind.JAxis = input;
                        bind.JButton = string.Empty;
                    }
                    else
                    {
                        bind.JButton = input;
                        bind.JAxis = string.Empty;
                    }
                    bind.AllReformers = new List<string>(modifiers);
                    bind.AdditionalImportInfo = title;
                    string alias;
                    bind.AliasJoystick = InternalDataManagement.JoystickAliases != null && InternalDataManagement.JoystickAliases.TryGetValue(device, out alias) ? alias ?? string.Empty : string.Empty;
                    target.bind = bind;
                }

                // Out of the source: the aircraft moved (DCS), else the node.
                if (dcs && planes.Count < active.Count)
                {
                    foreach (string plane in planes)
                        node.DeleteAircraftFromActivity(plane);
                }
                else
                    from.NODES.Remove(node);
                from.RecalculateElementCount();

                // Swap / replace: the action the aircraft had on the new input leaves it (swap: to the input the moved one came from).
                if (taken != null && ifTaken != "keep-both")
                    TakeOff(target, from, taken, game, dcs, ifTaken == "swap");

                if (dcs)
                {
                    foreach (string plane in planes)
                        target.AddNode(nodeId, game, target.ISAXIS, plane, false);
                }
                else
                {
                    // Every other game keeps the node as it was (its aircraft list, MSFS's limited aircraft).
                    RelationItem copy = node.Copy();
                    target.IncludeRelationItem(copy);
                }

                if (leavesEmpty)
                {
                    InternalDataManagement.AllRelations.Remove(fromName);
                    InternalDataManagement.AllBinds.Remove(fromName);
                }
                if (join == null)
                {
                    InternalDataManagement.AllRelations[targetName] = target;
                    InternalDataManagement.AllBinds[targetName] = target.bind;
                }
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }
            else
                CommandChangedNothing = true;

            result.Message = (dryRun ? "Would move '" : "Moved '") + title + "'" + (dcs ? " (" + string.Join(", ", planes) + ")" : string.Empty) + " from '" + fromName + "' to " +
                             (join != null ? "'" + targetName + "', the relation on " + where : "the new relation '" + targetName + "' on " + where) +
                             (result.Displaced.Count > 0
                                 ? "; " + string.Join(", ", result.Displaced.Select(t => "'" + t + "'")) + (ifTaken == "swap" ? " goes to '" + fromName + "'" : " leaves that input (not bound now)")
                                 : string.Empty) +
                             (result.BothFire ? "; '" + taken.Relation + "' stays on that input too, both fire together" : string.Empty) +
                             (leavesEmpty ? "; '" + fromName + "' had no other action and " + (dryRun ? "would go" : "is gone") + "." : ".");
            return EmitMoveResult(result, outPath, textOutput);
        }

        private static int EmitMoveResult(MoveToInputResult result, string outPath, bool textOutput)
        {
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(result.Message);
                sb.AppendLine("From: " + result.From);
                sb.AppendLine("To: " + result.To + (result.Joined ? " (joined)" : " (new)"));
                EmitPayload(sb.ToString(), outPath);
            }
            else
                EmitPayload(EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        private sealed class TakenInput
        {
            public string Relation { get; set; }
            public List<RelationItem> Nodes { get; set; }
            public List<string> Titles { get; set; }
            public List<string> Planes { get; set; }
        }

        // The relation on exactly this input (and modifiers) where the aircraft already have another action of the game; of
        // several, the one with most of the aircraft. null: none.
        private static TakenInput FindTakenInput(string except, string device, bool isAxis, string input, List<string> modifiers, string game, bool dcs, List<string> planes, string nodeId)
        {
            string signature = Games.GameProfileBridge.BuildSignature(device, isAxis, input, modifiers);
            TakenInput best = null;
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = entry.Value;
                if (relation == null || relation.ISAXIS != isAxis || string.Equals(entry.Key, except, StringComparison.OrdinalIgnoreCase))
                    continue;
                Bind bind = relation.bind;
                if ((bind == null || string.IsNullOrWhiteSpace(bind.Joystick)) && InternalDataManagement.AllBinds != null)
                    InternalDataManagement.AllBinds.TryGetValue(entry.Key, out bind);
                string bound = bind == null ? null : (isAxis ? bind.JAxis : bind.JButton);
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(bound) ||
                    !string.Equals(Games.GameProfileBridge.BuildSignature(bind.Joystick, isAxis, bound, bind.AllReformers), signature, StringComparison.OrdinalIgnoreCase))
                    continue;
                List<RelationItem> nodes = new List<RelationItem>();
                HashSet<string> overlap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem other in relation.NODES ?? new List<RelationItem>())
                {
                    if (other == null || string.IsNullOrWhiteSpace(other.ID) || string.Equals(other.ID.Trim(), nodeId, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(string.IsNullOrWhiteSpace(other.Game) ? "DCS" : other.Game.Trim(), game, StringComparison.OrdinalIgnoreCase))
                        continue;
                    List<string> hit = dcs ? other.GetActiveAircraftList().Where(p => planes.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList() : new List<string> { game };
                    if (dcs && hit.Count == 0)
                        continue;
                    if (!dcs && other.AIRCRAFT != null && other.AIRCRAFT.Count > 0 && !other.AIRCRAFT.Values.Any(v => v))
                        continue;
                    nodes.Add(other);
                    foreach (string p in hit)
                        overlap.Add(p);
                }
                if (nodes.Count == 0 || (best != null && overlap.Count <= best.Planes.Count))
                    continue;
                List<string> overlapList = overlap.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                best = new TakenInput
                {
                    Relation = entry.Key,
                    Nodes = nodes,
                    Planes = overlapList,
                    Titles = nodes.Select(n => MoveActionTitle(n, game, dcs ? n.GetActiveAircraftList().Where(p => overlap.Contains(p)).ToList() : overlapList))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
            }
            return best;
        }

        // Swap / replace: the actions the aircraft had in the taken relation leave it for them (DCS: those aircraft, else the
        // node); swap puts them into the relation the moved action came from.
        private static void TakeOff(Relation target, Relation from, TakenInput taken, string game, bool dcs, bool swap)
        {
            foreach (RelationItem other in taken.Nodes)
            {
                string otherId = other.ID.Trim();
                if (dcs)
                {
                    List<string> leave = other.GetActiveAircraftList().Where(p => taken.Planes.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (leave.Count >= other.GetActiveAircraftList().Count)
                        target.NODES.Remove(other);
                    else
                        foreach (string plane in leave)
                            other.DeleteAircraftFromActivity(plane);
                    if (swap)
                        foreach (string plane in leave)
                            from.AddNode(otherId, game, from.ISAXIS, plane, false);
                }
                else
                {
                    target.NODES.Remove(other);
                    if (swap)
                        from.IncludeRelationItem(other.Copy());
                }
            }
            target.RecalculateElementCount();
            from.RecalculateElementCount();
        }

        // The device as the profile spells it (the same text, or the same instance GUID, ignoring case); else as given.
        private static string ProfileDeviceSpelling(string device)
        {
            string wanted = (device ?? string.Empty).Trim();
            if (wanted.Length == 0 || InternalDataManagement.AllBinds == null)
                return wanted;
            string guid = DeviceGuidOf(wanted);
            string byGuid = null;
            foreach (Bind bind in InternalDataManagement.AllBinds.Values)
            {
                string known = bind == null ? null : (bind.Joystick ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(known))
                    continue;
                if (string.Equals(known, wanted, StringComparison.OrdinalIgnoreCase))
                    return known;
                if (byGuid == null && guid.Length > 0 && string.Equals(DeviceGuidOf(known), guid, StringComparison.OrdinalIgnoreCase))
                    byGuid = known;
            }
            return byGuid ?? wanted;
        }

        private static string DeviceGuidOf(string device)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(device ?? string.Empty, @"\{([0-9A-Fa-f-]{36})\}");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ModifierLabelOf(string reformer)
        {
            string[] parts = (reformer ?? string.Empty).Split('§');
            return parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : reformer;
        }

        // The action's readable title: DCS from the aircraft's list (or what the profile stored), other games from their
        // action catalog; else its id.
        private static string MoveActionTitle(RelationItem node, string game, List<string> planes)
        {
            string id = (node.ID ?? string.Empty).Trim();
            if (string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string plane in planes)
                {
                    string title = InternalDataManagement.DcsTitleOrEmpty(id, plane);
                    if (title.Length > 0)
                        return title;
                    DCSInput stored = (node.AllInputs ?? new DCSInput[0]).FirstOrDefault(i => i != null && string.Equals((i.Plane ?? string.Empty).Trim(), plane, StringComparison.OrdinalIgnoreCase));
                    if (stored != null && !DcsCommandNames.IsRawTitle(stored.Title, id))
                        return stored.Title.Trim();
                }
                return id;
            }
            Games.IGameIntegration integration;
            if (Games.GameRegistry.TryResolve(game, out integration) && integration != null)
            {
                Games.GameAction action = Games.GameCatalogStore.Load(integration, null).Find(id);
                if (action != null && !string.IsNullOrWhiteSpace(action.Title))
                    return action.Title.Trim();
                return EngineReadableNames.Title(integration.GameId, id);
            }
            OtherGameInput other = (node.OtherInputs ?? new OtherGameInput[0]).FirstOrDefault(i => i != null && !string.IsNullOrWhiteSpace(i.Title));
            return other != null ? other.Title.Trim() : id;
        }
    }
}
