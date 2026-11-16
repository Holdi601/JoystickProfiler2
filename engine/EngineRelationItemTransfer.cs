using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // Several actions at once from one relation to another (the relation editor's multi-select: Copy / Paste, Move to...,
    // and the Relations list's "Move their actions to...", docs/relation-multi-select.md):
    //   relations item copy-to <relation> --node game:id ... | --all  --to <relation>
    //   relations item move-to <relation> --node game:id ... | --all  --to <relation> | --new <name>  [--delete-empty]
    //   relations mass move-actions --to <relation> | --new <name>  --relation(s) ... [--delete-empty]
    // An action keeps its aircraft and which of them are switched off; an action the target already has gains the
    // aircraft it did not have. The target must be of the same kind (axis or button) unless --force; a new relation gets
    // the source's kind and no bind. Each command is one profile version (one Ctrl+Z).
    internal static partial class Program
    {
        private sealed class RelationItemTransferResult
        {
            public string Action { get; set; }
            public List<string> From { get; set; }
            public string To { get; set; }
            public bool Created { get; set; }
            public int Actions { get; set; }
            public List<string> Moved { get; set; }
            public List<string> DeletedRelations { get; set; }
            public List<string> Warnings { get; set; }

            public RelationItemTransferResult()
            {
                Action = string.Empty;
                From = new List<string>();
                To = string.Empty;
                Moved = new List<string>();
                DeletedRelations = new List<string>();
                Warnings = new List<string>();
            }
        }

        // relations item copy-to / move-to.
        private static int TransferRelationItems(string operation, string relationQuery, List<string> options, string outPath, bool textOutput)
        {
            bool move = operation == "move-to";
            List<string> nodeSpecs = PopOptionValues(options, "--node");
            bool all = PopFlag(options, "--all");
            string toQuery = PopOptionValue(options, "--to");
            string newName = move ? PopOptionValue(options, "--new") : null;
            bool deleteEmpty = move && PopFlag(options, "--delete-empty");
            bool force = PopFlag(options, "--force");
            if (options.Count > 0)
                return Fail("Unknown relations item " + operation + " argument(s): " + string.Join(" ", options));
            if (nodeSpecs.Count == 0 && !all)
                return Fail("relations item " + operation + " needs --all or at least one --node <game:id>.");
            if (string.IsNullOrWhiteSpace(toQuery) == string.IsNullOrWhiteSpace(newName))
                return Fail(move
                    ? "relations item move-to needs either --to <relation> or --new <name>."
                    : "relations item copy-to needs --to <relation>.");

            List<string> candidates;
            string fromName;
            if (!TryResolveRelationName(relationQuery, out fromName, out candidates))
                return FailResolution("relation", relationQuery, candidates);
            Relation from = InternalDataManagement.AllRelations[fromName];

            List<RelationItem> items = new List<RelationItem>();
            if (all)
                items.AddRange((from.NODES ?? new List<RelationItem>()).Where(n => n != null));
            foreach (string raw in nodeSpecs)
            {
                NodeSpec spec;
                if (!TryParseNodeSpec(raw, out spec))
                    return Fail("Invalid --node value '" + raw + "'. Use --node <game:id>.");
                List<RelationItem> matches = FindMatchingRelationItems(from, spec);
                if (matches.Count == 0)
                    return Fail("Relation '" + fromName + "' does not hold " + spec.Game + ":" + spec.Id + ".");
                foreach (RelationItem match in matches)
                {
                    if (!items.Contains(match))
                        items.Add(match);
                }
            }
            if (items.Count == 0)
                return Fail("Relation '" + fromName + "' has no action to " + (move ? "move" : "copy") + ".");

            RelationItemTransferResult result = new RelationItemTransferResult();
            result.Action = operation;
            result.From.Add(fromName);
            Relation target;
            string error;
            if (!ResolveTransferTarget(toQuery, newName, from.ISAXIS, force, new[] { fromName }, result, out target, out error))
                return Fail(error);

            AddTransferredItems(from, items, target, result);
            if (move)
            {
                foreach (RelationItem item in items)
                    from.NODES.Remove(item);
                from.RecalculateElementCount();
                if (deleteEmpty && from.NODES.Count == 0 && RemoveRelationByName(fromName))
                    result.DeletedRelations.Add(fromName);
            }
            FinishTransfer(result);
            return EmitTransferResult(result, outPath, textOutput);
        }

        // relations mass move-actions: every action of the selected relations into one relation.
        private static int RunRelationMassMoveActions(List<string> args, string outPath, bool textOutput)
        {
            string toQuery = PopOptionValue(args, "--to");
            string newName = PopOptionValue(args, "--new");
            bool deleteEmpty = PopFlag(args, "--delete-empty");
            bool force = PopFlag(args, "--force");
            if (string.IsNullOrWhiteSpace(toQuery) == string.IsNullOrWhiteSpace(newName))
                return Fail("relations mass move-actions needs either --to <relation> or --new <name>.");
            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass move-actions argument(s): " + string.Join(" ", args));

            bool isAxis = selected[0].ISAXIS;
            if (!force && selected.Any(r => r != null && r.ISAXIS != isAxis))
                return Fail("relations mass move-actions needs relations of one kind: all axis or all button (--force moves them anyway).");

            RelationItemTransferResult result = new RelationItemTransferResult();
            result.Action = "move-actions";
            Relation target;
            if (!ResolveTransferTarget(toQuery, newName, isAxis, force, selectedNames, result, out target, out error))
                return Fail(error);

            foreach (Relation source in selected)
            {
                if (source == null || ReferenceEquals(source, target))
                    continue;
                result.From.Add(source.NAME);
                List<RelationItem> items = (source.NODES ?? new List<RelationItem>()).Where(n => n != null).ToList();
                AddTransferredItems(source, items, target, result);
                source.NODES = new List<RelationItem>();
                source.RecalculateElementCount();
                if (deleteEmpty && RemoveRelationByName(source.NAME))
                    result.DeletedRelations.Add(source.NAME);
            }
            if (result.From.Count == 0)
                return Fail("The selected relations are the target itself: nothing to move.");
            FinishTransfer(result);
            return EmitTransferResult(result, outPath, textOutput);
        }

        // The relation the actions go to: an existing one (--to, of the same kind unless --force) or a new one (--new, the
        // source's kind, no bind).
        private static bool ResolveTransferTarget(string toQuery, string newName, bool isAxis, bool force, IEnumerable<string> sources, RelationItemTransferResult result,
            out Relation target, out string error)
        {
            target = null;
            error = null;
            if (!string.IsNullOrWhiteSpace(newName))
            {
                string name = newName.Trim();
                if (InternalDataManagement.AllRelations.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "A relation named '" + name + "' already exists: use --to '" + name + "' or another name.";
                    return false;
                }
                target = new Relation();
                target.NAME = name;
                target.ISAXIS = isAxis;
                target.NODES = new List<RelationItem>();
                InternalDataManagement.AllRelations[name] = target;
                result.To = name;
                result.Created = true;
                return true;
            }

            List<string> candidates;
            string toName;
            if (!TryResolveRelationName(toQuery, out toName, out candidates))
            {
                error = BuildResolutionErrorMessage("relation", toQuery, candidates);
                return false;
            }
            if (sources.Count() == 1 && sources.Any(s => string.Equals(s, toName, StringComparison.OrdinalIgnoreCase)))
            {
                error = "'" + toName + "' is the relation the actions are in: pick another one.";
                return false;
            }
            target = InternalDataManagement.AllRelations[toName];
            if (target.ISAXIS != isAxis && !force)
            {
                error = "'" + toName + "' is " + (target.ISAXIS ? "an axis relation" : "a button relation") + "; the actions are " +
                        (isAxis ? "axis actions" : "button actions") + " (--force adds them anyway).";
                return false;
            }
            if (target.NODES == null)
                target.NODES = new List<RelationItem>();
            result.To = toName;
            return true;
        }

        private static void AddTransferredItems(Relation source, List<RelationItem> items, Relation target, RelationItemTransferResult result)
        {
            foreach (RelationItem item in items)
            {
                string game = string.IsNullOrWhiteSpace(item.Game) ? "DCS" : item.Game.Trim();
                // In a relation an aircraft has one action of a game: say so when the target already has another one there.
                List<string> planes = item.GetActiveAircraftList();
                List<string> clash = (target.NODES ?? new List<RelationItem>())
                    .Where(n => n != null && !string.Equals(n.ID, item.ID, StringComparison.Ordinal) &&
                                string.Equals(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game.Trim(), game, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(n => n.GetActiveAircraftList())
                    .Intersect(planes, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (clash.Count > 0)
                    result.Warnings.Add("'" + target.NAME + "' already has another " + game + " action for " + string.Join(", ", clash.Take(5)) +
                                        (clash.Count > 5 ? " (+" + (clash.Count - 5).ToString(CultureInfo.InvariantCulture) + ")" : string.Empty) +
                                        ": both are in it now (" + game + ":" + item.ID + " from '" + source.NAME + "').");
                target.IncludeRelationItem(item.Copy());
                result.Moved.Add(game + ":" + item.ID);
                result.Actions++;
            }
            target.RecalculateElementCount();
        }

        private static void FinishTransfer(RelationItemTransferResult result)
        {
            // One operation of the user: a version of its own, so one Ctrl+Z takes exactly it back.
            EngineProfileHistory.NoCoalesce = true;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static int EmitTransferResult(RelationItemTransferResult result, string outPath, bool textOutput)
        {
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(result), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            string verb = result.Action == "copy-to" ? "Copied" : "Moved";
            sb.AppendLine(verb + " " + result.Actions.ToString(CultureInfo.InvariantCulture) + " action(s) from " +
                          string.Join(", ", result.From.Select(f => "'" + f + "'")) + " to '" + result.To + "'" + (result.Created ? " (new relation)." : "."));
            foreach (string moved in result.Moved)
                sb.AppendLine("  " + moved);
            foreach (string deleted in result.DeletedRelations)
                sb.AppendLine("Deleted the relation '" + deleted + "' (no action left).");
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }
    }
}
