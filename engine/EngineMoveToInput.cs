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
            if (options.Count > 0)
                return Fail("Unknown relations item move-to-input argument(s): " + string.Join(" ", options));
            if (nodeSpecs.Count != 1)
                return Fail("relations item move-to-input needs exactly one --node <game:id[:plane1|plane2]>.");
            if (device.Length == 0 || input.Length == 0)
                return Fail("relations item move-to-input needs --device <device> and --input <button or axis>.");

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

            // Whether the source keeps any action afterwards (a relation left empty goes away with its bind, as after a take-over).
            bool leavesEmpty = (from.NODES ?? new List<RelationItem>()).All(n => n == null || ReferenceEquals(n, node)) &&
                               (!dcs || planes.Count >= active.Count);
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
                DryRun = dryRun
            };

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

            string where = device + " " + input + (modifiers.Count > 0 ? " (+" + string.Join(", ", modifiers.Select(ModifierLabelOf)) + ")" : string.Empty);
            result.Message = (dryRun ? "Would move '" : "Moved '") + title + "'" + (dcs ? " (" + string.Join(", ", planes) + ")" : string.Empty) + " from '" + fromName + "' to " +
                             (join != null ? "'" + targetName + "', the relation on " + where : "the new relation '" + targetName + "' on " + where) +
                             (leavesEmpty ? "; '" + fromName + "' had no other action and " + (dryRun ? "would go" : "is gone") + "." : ".");

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
