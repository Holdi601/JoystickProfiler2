using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.Engine
{
    internal static partial class Program
    {
        private static readonly HashSet<string> DuplicatesMutatingFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--keep", "--off", "--keep-both", "--forget-kept", "--apply", "--apply-suggested", "--changes"
        };

        // relations duplicates: the actions of aircraft that more than one relation has switched on (the game gets them on
        // each input), as cards per relation pair with a suggestion and its confidence (EngineRelationDuplicates).
        //   [--keys "game|id|plane;..."] or [--node game:id --plane p1|p2]   the actions a fix is about
        //   --keep <relation> | --off | --keep-both                          one card's fix (one undo step)
        //   --forget-kept                                                    warn again about everything kept on purpose
        //   --rule suggested|name|first|device:<kind>|relation:<words> [--min-confidence N] [--apply]
        //   --instruction "<words>" [--llm off|auto|on] [--apply]           a fix for many in words (phrase rules, local AI model)
        //   --apply-suggested [--min-confidence N]                          = --rule suggested --apply
        //   --changes "game|id|plane=>relation;...=>-"                      a preview's ticked changes exactly as shown
        //   --include-kept, --no-history, --evaluate <truth.pr0file>        listing and calibration
        private static int RelationDuplicatesCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool applySuggested = PopFlag(args, "--apply-suggested");
            bool apply = PopFlag(args, "--apply") || applySuggested;
            bool offEverywhere = PopFlag(args, "--off");
            bool keepBoth = PopFlag(args, "--keep-both");
            bool forgetKept = PopFlag(args, "--forget-kept");
            bool noHistory = PopFlag(args, "--no-history");
            bool includeKept = PopFlag(args, "--include-kept");
            bool keysOnly = PopFlag(args, "--keys-only");
            string keep = PopOptionValue(args, "--keep");
            string nodeOption = PopOptionValue(args, "--node");
            string planeOption = PopOptionValue(args, "--plane");
            string keysOption = PopOptionValue(args, "--keys");
            string rule = PopOptionValue(args, "--rule");
            string instruction = PopOptionValue(args, "--instruction");
            string llm = (PopOptionValue(args, "--llm") ?? "auto").Trim().ToLowerInvariant();
            string minText = PopOptionValue(args, "--min-confidence");
            string evaluate = PopOptionValue(args, "--evaluate");
            string changesOption = PopOptionValue(args, "--changes");
            if (args.Count > 0)
                return Fail("Unknown relations duplicates argument(s): " + string.Join(" ", args) +
                            ". Usage: relations duplicates [--keys k;k | --node game:id --plane p1|p2] [--keep <relation> | --off | --keep-both] [--rule <rule> | --instruction \"...\"] [--min-confidence N] [--apply]");
            if (new[] { keep != null, offEverywhere, keepBoth }.Count(b => b) > 1)
                return Fail("relations duplicates: --keep, --off and --keep-both exclude each other.");
            if (!EngineNlGroup.LlmModes.Contains(llm))
                return Fail("Invalid --llm value: " + llm + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
            int minConfidence = 0;
            if (minText != null && !int.TryParse(minText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minConfidence))
                return Fail("--min-confidence needs a number from 0 to 100.");
            if (applySuggested && rule == null && instruction == null)
                rule = "suggested";
            if (rule != null && instruction != null)
                return Fail("relations duplicates: --rule and --instruction exclude each other.");
            if (rule != null && !IsDuplicateRule(rule))
                return Fail("Unknown rule '" + rule + "'. Use suggested, name, first, device:<stick|throttle|panel|pedals|collective> or relation:<words>.");

            EngineRelationDuplicates.UseHistory = !noHistory;
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in (keysOption ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                keys.Add(key.Trim());
            if (!string.IsNullOrWhiteSpace(nodeOption))
            {
                string spec = nodeOption.Trim();
                int colon = spec.IndexOf(':');
                if (colon <= 0 || string.IsNullOrWhiteSpace(planeOption))
                    return Fail("--node needs game:id and --plane <aircraft>.");
                foreach (string plane in planeOption.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    keys.Add(EngineRelationDuplicates.KeyOf(spec.Substring(0, colon), spec.Substring(colon + 1), plane.Trim()));
            }

            EngineDuplicatesResult result = new EngineDuplicatesResult();
            if (keysOnly)
            {
                List<string> found = EngineRelationDuplicates.FindKeys();
                result.Count = found.Count;
                result.Duplicates = found.Select(k => new EngineDuplicateAction { Key = k }).ToList();
                EmitPayload(textOutput ? string.Join(Environment.NewLine, found) : EngineSerialization.ToJson(result), outPath);
                return 0;
            }
            List<EngineDuplicateAction> all = EngineRelationDuplicates.Find(includeKept: true);
            List<EngineDuplicateAction> open = all.Where(d => !d.Kept).ToList();
            if (evaluate != null)
            {
                string truthPath = NormalizePath(evaluate);
                if (!File.Exists(truthPath))
                    return Fail("--evaluate needs a profile with the right answers: " + evaluate);
                Pr0file truth = InternalDataManagement.ReadProfileBytes(File.ReadAllBytes(truthPath));
                List<string> wrong = new List<string>();
                result.Evaluation = EngineRelationDuplicates.Evaluate(open, truth, wrong);
                result.Warnings.AddRange(wrong.OrderByDescending(w => w, StringComparer.Ordinal).Take(300));
                result.Count = open.Count;
                EmitPayload(textOutput ? EngineRelationDuplicates.ToText(result) : EngineSerialization.ToJson(result), outPath);
                return 0;
            }

            bool changedProfile = false;
            if (keep != null || offEverywhere || keepBoth)
            {
                if (keys.Count == 0)
                    return Fail("relations duplicates --keep/--off/--keep-both needs the actions: --keys \"game|id|aircraft;...\" or --node game:id --plane <aircraft>.");
                List<EngineDuplicateAction> targets = all.Where(d => keys.Contains(d.Key)).ToList();
                if (targets.Count == 0)
                    return Fail("Those actions are on in one relation only (nothing to resolve).");
                string relationName = null;
                if (keep != null)
                {
                    List<string> candidates;
                    if (!TryResolveRelationName(keep, out relationName, out candidates))
                        return FailResolution("relation", keep, candidates);
                    EngineDuplicateAction without = targets.FirstOrDefault(t => !t.Places.Any(p => string.Equals(p.Relation, relationName, StringComparison.OrdinalIgnoreCase)));
                    if (without != null)
                        return Fail("Relation '" + relationName + "' does not have '" + without.Title + "' of " + without.Plane + " switched on.");
                }
                if (keepBoth)
                {
                    EngineRelationDuplicates.KeepBoth(targets);
                }
                else
                {
                    foreach (EngineDuplicateAction target in targets)
                        result.SwitchedOff += EngineRelationDuplicates.KeepOnly(target.Game, target.Id, target.Plane, relationName).Count;
                }
                result.Resolved += targets.Count;
                changedProfile = true;
                if (string.IsNullOrWhiteSpace(EngineProfileHistory.Note))
                    EngineProfileHistory.Note = keepBoth ? "Keeping actions in several relations" : relationName != null ? "Keeping actions in '" + relationName + "' only" : "Switching actions off in every relation";
            }
            // The changes of a preview exactly as shown (the ticked ones): "key=>relation" or "key=>-" (off in all).
            if (changesOption != null)
            {
                foreach (string item in changesOption.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int arrow = item.IndexOf("=>", StringComparison.Ordinal);
                    if (arrow <= 0)
                        return Fail("--changes needs \"game|id|aircraft=>relation;...\" (=>- switches it off in all).");
                    string key = item.Substring(0, arrow).Trim();
                    string target = item.Substring(arrow + 2).Trim();
                    EngineDuplicateAction duplicate = all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (duplicate == null)
                    {
                        result.Warnings.Add("Not on in several relations any more: " + key);
                        continue;
                    }
                    string keepIn = target == "-" ? null : duplicate.Places.Select(p => p.Relation).FirstOrDefault(r => string.Equals(r, target, StringComparison.OrdinalIgnoreCase));
                    if (target != "-" && keepIn == null)
                        return Fail("Relation '" + target + "' does not have '" + duplicate.Title + "' of " + duplicate.Plane + " switched on.");
                    result.SwitchedOff += EngineRelationDuplicates.KeepOnly(duplicate.Game, duplicate.Id, duplicate.Plane, keepIn).Count;
                    result.Resolved++;
                }
                result.Applied = true;
                changedProfile = changedProfile || result.Resolved > 0;
                if (result.Resolved > 0 && string.IsNullOrWhiteSpace(EngineProfileHistory.Note))
                    EngineProfileHistory.Note = "Resolving " + result.Resolved.ToString(CultureInfo.InvariantCulture) + " actions on in several relations";
            }
            if (forgetKept)
            {
                result.Resolved += InternalDataManagement.KeptDuplicates == null ? 0 : InternalDataManagement.KeptDuplicates.Count;
                InternalDataManagement.KeptDuplicates = new List<string>();
                changedProfile = true;
            }
            if (rule != null || instruction != null)
            {
                EngineRelationDuplicates.Understanding understanding = null;
                if (instruction != null)
                {
                    understanding = EngineRelationDuplicates.ReadInstruction(instruction);
                    EngineRelationDuplicates.AskModel(understanding, llm, (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Keys.ToList(), result.Warnings);
                    result.Instruction = instruction;
                    result.Understood = understanding.Rules.Select(r => r.Text).ToList();
                    result.NotUnderstood = understanding.NotUnderstood.ToList();
                    result.Method = understanding.Method;
                }
                result.Rule = instruction != null ? "instruction" : rule.Trim();
                foreach (EngineDuplicateAction duplicate in open.Where(d => keys.Count == 0 || keys.Contains(d.Key)))
                {
                    EngineDuplicateChange change = understanding != null
                        ? EngineRelationDuplicates.ProposeByInstruction(duplicate, understanding)
                        : EngineRelationDuplicates.Propose(duplicate, rule, minConfidence);
                    if (change != null)
                        result.Changes.Add(change);
                }
                if (apply)
                {
                    foreach (EngineDuplicateChange change in result.Changes)
                    {
                        result.SwitchedOff += EngineRelationDuplicates.KeepOnly(change.Game, change.Id, change.Plane, change.Off ? null : change.Keep).Count;
                        result.Resolved++;
                    }
                    result.Applied = true;
                    changedProfile = changedProfile || result.Changes.Count > 0;
                    if (result.Changes.Count > 0 && string.IsNullOrWhiteSpace(EngineProfileHistory.Note))
                        EngineProfileHistory.Note = "Resolving " + result.Changes.Count.ToString(CultureInfo.InvariantCulture) + " actions on in several relations (" + result.Rule + ")";
                }
            }
            if (changedProfile)
            {
                InternalDataManagement.ResyncRelations();
                all = EngineRelationDuplicates.Find(includeKept: true);
            }
            result.Duplicates = all.Where(d => includeKept || !d.Kept).ToList();
            result.Groups = EngineRelationDuplicates.Group(result.Duplicates);
            result.KeptCount = all.Count(d => d.Kept);
            result.Count = all.Count(d => !d.Kept);
            result.WithSuggestion = all.Count(d => !d.Kept && (d.Suggested.Length > 0 || d.SuggestOff));
            EmitPayload(textOutput ? EngineRelationDuplicates.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        private static bool IsDuplicateRule(string rule)
        {
            string r = (rule ?? string.Empty).Trim().ToLowerInvariant();
            return r == "suggested" || r == "name" || r == "first" ||
                   (r.StartsWith("device:", StringComparison.Ordinal) && r.Length > "device:".Length) ||
                   (r.StartsWith("relation:", StringComparison.Ordinal) && r.Length > "relation:".Length);
        }
    }
}
