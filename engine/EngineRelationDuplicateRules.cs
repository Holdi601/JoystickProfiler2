using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Fixing many duplicates at once (engine relations duplicates --rule / --instruction): a rule picks for each action
    // of an aircraft the relation it stays in (or none); the preview lists every change, --apply makes them (one profile
    // version, one undo step).
    //   suggested         the suggestion, from --min-confidence on;
    //   name              the relation whose function or name is the action's function (the function chip, the function map);
    //   first             where it was before the second relation got it (the profile history);
    //   device:<kind>     the relation bound on a device of that kind (stick, throttle, panel, pedals, collective);
    //   relation:<words>  the relation whose name has these words.
    // An instruction in words ("keep trim functions in the Trim relations, everything else in the Mode Button relations")
    // is read by phrase rules; only what they do not understand goes to the local AI model, which rewrites it into those
    // phrases (as Lay out with AI does), never into changes of its own.
    internal static partial class EngineRelationDuplicates
    {
        internal sealed class PhraseRule
        {
            // Words the action's title or function must have; empty: every action ("everything else").
            public List<string> What = new List<string>();
            // Words of the relation to keep it in; empty with Off: switch it off in all.
            public List<string> Where = new List<string>();
            public bool Off;
            public string DeviceClass = string.Empty;
            public string Text = string.Empty;
        }

        internal sealed class Understanding
        {
            public List<PhraseRule> Rules = new List<PhraseRule>();
            public List<string> NotUnderstood = new List<string>();
            public string Method = "JoyPro's rules";
        }

        private static readonly string[] DeviceClasses = { "stick", "throttle", "panel", "pedals", "collective" };

        private static readonly Dictionary<string, string> DeviceWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "stick", "stick" }, { "sticks", "stick" }, { "joystick", "stick" }, { "grip", "stick" }, { "base", "stick" },
            { "throttle", "throttle" }, { "throttles", "throttle" },
            { "panel", "panel" }, { "panels", "panel" }, { "button box", "panel" }, { "button boxes", "panel" }, { "box", "panel" }, { "boxes", "panel" }, { "mfd", "panel" }, { "mfds", "panel" },
            { "pedals", "pedals" }, { "pedal", "pedals" }, { "rudder", "pedals" }, { "collective", "collective" }
        };

        // The change a rule makes for one duplicate (null: the rule does not decide it).
        internal static EngineDuplicateChange Propose(EngineDuplicateAction duplicate, string rule, int minConfidence)
        {
            string r = (rule ?? "suggested").Trim();
            string lower = r.ToLowerInvariant();
            if (lower == "suggested")
            {
                if (duplicate.Confidence < minConfidence || (duplicate.Suggested.Length == 0 && !duplicate.SuggestOff))
                    return null;
                return Change(duplicate, duplicate.SuggestOff ? null : duplicate.Suggested, duplicate.Confidence, "suggested");
            }
            if (lower == "name")
            {
                List<EngineDuplicatePlace> named = duplicate.Places.Where(p => p.FunctionMatch == "function" || p.FunctionMatch == "name").ToList();
                if (named.Count != 1)
                    return null;
                int confidence = string.Equals(duplicate.Suggested, named[0].Relation, StringComparison.OrdinalIgnoreCase) ? Math.Max(duplicate.Confidence, 70) : 70;
                return Change(duplicate, named[0].Relation, confidence, "name");
            }
            if (lower == "first")
            {
                if (duplicate.FirstIn.Length == 0)
                    return null;
                return Change(duplicate, duplicate.FirstIn == "-" ? null : duplicate.FirstIn, 90, "first");
            }
            if (lower.StartsWith("device:", StringComparison.Ordinal))
            {
                string kind = NormalizeDeviceClass(r.Substring("device:".Length));
                List<EngineDuplicatePlace> on = duplicate.Places.Where(p => string.Equals(p.DeviceClass, kind, StringComparison.OrdinalIgnoreCase)).ToList();
                return on.Count == 1 ? Change(duplicate, on[0].Relation, 80, "device:" + kind) : null;
            }
            if (lower.StartsWith("relation:", StringComparison.Ordinal))
            {
                EngineDuplicatePlace place = PlaceByWords(duplicate, Words(r.Substring("relation:".Length)).ToList());
                return place == null ? null : Change(duplicate, place.Relation, 85, "relation:" + r.Substring("relation:".Length).Trim());
            }
            return null;
        }

        internal static string NormalizeDeviceClass(string text)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            string kind;
            return DeviceWords.TryGetValue(t, out kind) ? kind : t;
        }

        private static EngineDuplicatePlace PlaceByWords(EngineDuplicateAction duplicate, List<string> words)
        {
            if (words.Count == 0)
                return null;
            List<EngineDuplicatePlace> matching = duplicate.Places.Where(p =>
            {
                HashSet<string> name = Words(p.Relation);
                return words.All(name.Contains);
            }).ToList();
            return matching.Count == 1 ? matching[0] : null;
        }

        private static EngineDuplicateChange Change(EngineDuplicateAction duplicate, string keep, int confidence, string rule)
        {
            EngineDuplicateChange change = new EngineDuplicateChange
            {
                Key = duplicate.Key,
                Game = duplicate.Game,
                Id = duplicate.Id,
                Plane = duplicate.Plane,
                Title = duplicate.Title,
                Keep = keep ?? string.Empty,
                Off = keep == null,
                Confidence = confidence,
                Rule = rule
            };
            change.SwitchOffIn = duplicate.Places.Select(p => p.Relation).Where(n => keep == null || !string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)).ToList();
            return change;
        }

        // The change of an instruction's rules for one duplicate: the first rule whose words fit the action and whose
        // relation words name exactly one of its relations.
        internal static EngineDuplicateChange ProposeByInstruction(EngineDuplicateAction duplicate, Understanding understanding)
        {
            foreach (PhraseRule rule in understanding.Rules)
            {
                if (rule.What.Count > 0 && !ActionMatches(duplicate, rule.What))
                    continue;
                if (rule.DeviceClass.Length > 0)
                {
                    List<EngineDuplicatePlace> on = duplicate.Places.Where(p => string.Equals(p.DeviceClass, rule.DeviceClass, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (on.Count == 1)
                        return Change(duplicate, on[0].Relation, 80, "instruction: " + rule.Text);
                    continue;
                }
                if (rule.Off)
                    return Change(duplicate, null, 80, "instruction: " + rule.Text);
                EngineDuplicatePlace place = PlaceByWords(duplicate, rule.Where);
                if (place != null)
                    return Change(duplicate, place.Relation, 85, "instruction: " + rule.Text);
            }
            return null;
        }

        // The action's title has the words (stemmed), or the function map reads the words as one of its functions.
        private static bool ActionMatches(EngineDuplicateAction duplicate, List<string> what)
        {
            HashSet<string> title = Words(duplicate.Title);
            if (what.All(title.Contains))
                return true;
            try
            {
                EngineFunctionMap.Map map = EngineFunctionMap.Load(null);
                EngineFunctionMap.Understanding u = EngineFunctionMap.Resolve(map, string.Join(" ", what));
                string g = EngineRelationSuggest.GameKey(duplicate.Game);
                EngineFunctionMap.Entry entry;
                if (u.Functions.Count == 0 || !map.ByKey.TryGetValue(g + "|" + EngineRelationSuggest.NodeId(g, duplicate.Id) + "|" + (g == "DCS" ? duplicate.Plane : g), out entry))
                    return false;
                return u.Functions.Where(f => !f.Related && f.Function != null)
                    .Any(f => entry.Functions.Any(a => a.Function != null && a.Weight >= 0.5 && string.Equals(a.Function.Id, f.Function.Id, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly Regex KeepIn = new Regex(@"^(?:please\s+)?(?:keep|put|leave|move|place)\s+(?<what>.+?)\s+(?:in|into|on|to|at)\s+(?:the\s+|my\s+)?(?<where>.+?)$", RegexOptions.IgnoreCase);
        private static readonly Regex GoesIn = new Regex(@"^(?<what>.+?)\s+(?:go|goes|stay|stays|belong|belongs|should\s+be|should\s+stay|stays?)\s+(?:in|into|to|on|with)\s+(?:the\s+|my\s+)?(?<where>.+?)$", RegexOptions.IgnoreCase);
        private static readonly Regex RestIn = new Regex(@"^(?:and\s+)?(?:keep\s+|put\s+)?(?<what>everything\s+else|all\s+(?:the\s+)?(?:rest|others?)|the\s+rest|the\s+others|others|rest|otherwise|else|everything)\s+(?:in|into|to|on|goes\s+to|goes\s+in|stays\s+in)\s+(?:the\s+|my\s+)?(?<where>.+?)$", RegexOptions.IgnoreCase);
        private static readonly Regex OffRule = new Regex(@"^(?:switch|turn)\s+(?<what>.+?)\s+off(?:\s+everywhere|\s+in\s+all(?:\s+of\s+them)?)?$|^(?:switch\s+off|turn\s+off|remove|drop|delete)\s+(?<what2>.+?)(?:\s+everywhere|\s+in\s+all(?:\s+of\s+them)?)?$", RegexOptions.IgnoreCase);
        private static readonly Regex PreferDevice = new Regex(@"^(?:prefer|use|keep\s+(?:it|them|everything|all)\s+on)\s+(?:the\s+|my\s+)?(?<kind>sticks?|joystick|grip|base|throttles?|panels?|button\s+box(?:es)?|box|mfds?|pedals?|rudder|collective)$", RegexOptions.IgnoreCase);

        private static readonly HashSet<string> WhatFiller = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "all", "my", "functions", "function", "actions", "action", "binds", "bind", "commands", "command", "ones", "stuff", "things", "buttons"
        };

        private static readonly HashSet<string> WhereFiller = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "my", "relation", "relations", "one", "ones", "group", "groups"
        };

        // The phrases JoyPro's rules read: one clause per rule, in order; "everything else" catches the rest.
        internal static Understanding ReadInstruction(string instruction)
        {
            Understanding u = new Understanding();
            foreach (string raw in Regex.Split(instruction ?? string.Empty, @"[\r\n;.]+|,\s*|\s+then\s+|\s+and\s+(?=(?:keep|put|leave|move|switch|turn|remove|prefer|everything|the\s+rest|all\s+others|others)\b)", RegexOptions.IgnoreCase))
            {
                string clause = raw.Trim().Trim('"', '\'', '-', '*', ' ');
                if (clause.Length == 0)
                    continue;
                PhraseRule rule = ReadClause(clause);
                if (rule == null)
                    u.NotUnderstood.Add(clause);
                else
                    u.Rules.Add(rule);
            }
            // "everything else" last, whatever its place in the text.
            u.Rules = u.Rules.Where(r => r.What.Count > 0 || r.DeviceClass.Length > 0).Concat(u.Rules.Where(r => r.What.Count == 0 && r.DeviceClass.Length == 0)).ToList();
            return u;
        }

        private static PhraseRule ReadClause(string clause)
        {
            Match m = PreferDevice.Match(clause);
            if (m.Success)
                return new PhraseRule { DeviceClass = NormalizeDeviceClass(Regex.Replace(m.Groups["kind"].Value, @"\s+", " ")), Text = clause };
            m = RestIn.Match(clause);
            if (m.Success)
                return WhereRule(new List<string>(), m.Groups["where"].Value, clause);
            m = OffRule.Match(clause);
            if (m.Success)
            {
                string what = m.Groups["what"].Success && m.Groups["what"].Value.Length > 0 ? m.Groups["what"].Value : m.Groups["what2"].Value;
                List<string> words = WhatWords(what);
                return words.Count == 0 ? null : new PhraseRule { What = words, Off = true, Text = clause };
            }
            m = KeepIn.Match(clause);
            if (!m.Success)
                m = GoesIn.Match(clause);
            if (m.Success)
            {
                string whatText = m.Groups["what"].Value;
                if (Regex.IsMatch(whatText, @"^(?:everything\s+else|the\s+rest|all\s+others|others|rest)$", RegexOptions.IgnoreCase))
                    return WhereRule(new List<string>(), m.Groups["where"].Value, clause);
                // "keep it on the throttle": a device kind.
                string kind;
                string whereText = Regex.Replace(m.Groups["where"].Value.Trim().ToLowerInvariant(), @"^(?:the|my)\s+", string.Empty);
                if (Regex.IsMatch(whatText, @"^(?:it|them|everything|all)$", RegexOptions.IgnoreCase) && DeviceWords.TryGetValue(whereText, out kind))
                    return new PhraseRule { DeviceClass = kind, Text = clause };
                List<string> what = WhatWords(whatText);
                if (what.Count == 0)
                    return null;
                return WhereRule(what, m.Groups["where"].Value, clause);
            }
            return null;
        }

        private static PhraseRule WhereRule(List<string> what, string where, string clause)
        {
            List<string> words = Words(Regex.Replace(where ?? string.Empty, @"\b(?:relations?|ones?|groups?)\b", " ", RegexOptions.IgnoreCase))
                .Where(w => !WhereFiller.Contains(w)).ToList();
            if (words.Count == 0)
                return null;
            return new PhraseRule { What = what, Where = words, Text = clause };
        }

        private static List<string> WhatWords(string what)
        {
            return EngineFunctionMap.Tokenize(what ?? string.Empty).Where(t => t.Length > 1 && !WhatFiller.Contains(t) && !Filler.Contains(t))
                .Select(EngineFunctionMap.Stem).Distinct().ToList();
        }

        private const string InstructionPrompt =
            "You rewrite a flight simulator user's instruction about controls that are bound twice into short English phrases of exactly these forms, one per item:\n" +
            "keep <words of the actions> in <words of the relation names>\n" +
            "everything else in <words of the relation names>\n" +
            "switch off <words of the actions>\n" +
            "prefer <stick|throttle|panel|pedals|collective>\n" +
            "Use the user's own words for actions and relation names. Answer only with JSON: {\"phrases\":[\"...\"]}.";

        // The local AI model rewrites what the phrase rules did not understand into the phrases they know.
        internal static void AskModel(Understanding understood, string mode, List<string> relationNames, List<string> warnings)
        {
            if (understood.NotUnderstood.Count == 0 || mode == "off")
                return;
            EngineLlm.ModelInfo model;
            string problem;
            if (!EngineLlm.TryGetUsableModel(out model, out problem))
            {
                warnings.Add("The local AI model was not asked: " + problem);
                return;
            }
            List<EngineLlm.Message> messages = new List<EngineLlm.Message>
            {
                new EngineLlm.Message { Role = "system", Content = InstructionPrompt },
                new EngineLlm.Message { Role = "user", Content = "Relations: Trim Up, Trim Down, Mode Button 01, Mode Button 02\nInstruction: Alles was mit Trimmung zu tun hat in die Trim-Relationen, den Rest auf die Mode-Buttons." },
                new EngineLlm.Message { Role = "assistant", Content = "{\"phrases\":[\"keep trim in trim\",\"everything else in mode button\"]}" },
                new EngineLlm.Message { Role = "user", Content = "Relations: " + string.Join(", ", relationNames.Take(60)) + "\nInstruction: " + string.Join(". ", understood.NotUnderstood) }
            };
            EngineProgress.Step(0, 2, "Starting the local AI model", EngineRelationPlan.ModelName(model));
            EngineLlm.Session session = EngineLlm.Session.Start(model, EngineRelationPlan.PlanContextSize(model));
            try
            {
                if (session.Error.Length > 0)
                {
                    warnings.Add("The local AI model did not start: " + session.Error);
                    return;
                }
                EngineProgress.Step(1, 2, "The local AI model reads your instruction", EngineRelationPlan.ModelName(model));
                EngineLlm.CallResult call = session.Ask(messages, null, 300, false);
                if (!string.IsNullOrEmpty(call.Error))
                {
                    warnings.Add("The local AI model did not answer: " + call.Error);
                    return;
                }
                List<string> phrases = new List<string>();
                try
                {
                    Dictionary<string, object> answer = EngineSerialization.FromJson<Dictionary<string, object>>(call.Content ?? string.Empty);
                    object list;
                    if (answer != null && answer.TryGetValue("phrases", out list) && list is System.Collections.IEnumerable)
                        phrases.AddRange(((System.Collections.IEnumerable)list).Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).Where(s => !string.IsNullOrWhiteSpace(s)));
                }
                catch (Exception)
                {
                    phrases.AddRange(Regex.Split(call.Content ?? string.Empty, @"[\r\n]+").Select(l => l.Trim(' ', '-', '*', '"')).Where(l => l.Length > 0));
                }
                Understanding rewritten = ReadInstruction(string.Join("\n", phrases));
                if (rewritten.Rules.Count > 0)
                {
                    understood.Rules.AddRange(rewritten.Rules);
                    understood.Rules = understood.Rules.Where(r => r.What.Count > 0 || r.DeviceClass.Length > 0).Concat(understood.Rules.Where(r => r.What.Count == 0 && r.DeviceClass.Length == 0)).ToList();
                    understood.NotUnderstood = rewritten.NotUnderstood;
                    understood.Method = "JoyPro's rules and the local AI model (" + EngineRelationPlan.ModelName(model) + ")";
                }
            }
            finally
            {
                session.Dispose();
            }
        }

        // engine relations duplicates --evaluate <truth.pr0file>: the suggestions against a profile that has the
        // right answer (the version before the duplicates came): per threshold how many suggestions are right.
        internal static List<EngineDuplicateEvaluationRow> Evaluate(List<EngineDuplicateAction> duplicates, Pr0file truth, List<string> wrongLines)
        {
            Dictionary<string, HashSet<string>> owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in truth.Relations ?? new Dictionary<string, Relation>())
            {
                Relation relation = entry.Value;
                if (relation == null)
                    continue;
                string name = string.IsNullOrWhiteSpace(relation.NAME) ? entry.Key : relation.NAME;
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT ?? new Dictionary<string, bool>())
                    {
                        if (!plane.Value)
                            continue;
                        string key = KeyOf(node.Game, node.ID, plane.Key);
                        HashSet<string> set;
                        if (!owners.TryGetValue(key, out set))
                            owners[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(name);
                    }
                }
            }
            List<KeyValuePair<EngineDuplicateAction, string>> cases = new List<KeyValuePair<EngineDuplicateAction, string>>();
            foreach (EngineDuplicateAction duplicate in duplicates)
            {
                HashSet<string> set;
                owners.TryGetValue(duplicate.Key, out set);
                List<string> had = duplicate.Places.Select(p => p.Relation).Where(r => set != null && set.Contains(r)).ToList();
                if (had.Count >= 2)
                    continue;
                cases.Add(new KeyValuePair<EngineDuplicateAction, string>(duplicate, had.Count == 1 ? had[0] : "-"));
            }
            List<EngineDuplicateEvaluationRow> rows = new List<EngineDuplicateEvaluationRow>();
            foreach (int threshold in new[] { 0, 50, 60, 70, 80, 85, 90, 95 })
            {
                EngineDuplicateEvaluationRow row = new EngineDuplicateEvaluationRow { Threshold = threshold, Cases = cases.Count };
                foreach (KeyValuePair<EngineDuplicateAction, string> c in cases)
                {
                    EngineDuplicateAction d = c.Key;
                    if ((d.Suggested.Length == 0 && !d.SuggestOff) || d.Confidence < Math.Max(1, threshold))
                        continue;
                    row.Proposals++;
                    string proposed = d.SuggestOff ? "-" : d.Suggested;
                    EngineDuplicatePlace kept = d.Places.FirstOrDefault(p => string.Equals(p.Relation, proposed, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(proposed, c.Value, StringComparison.OrdinalIgnoreCase))
                        row.Correct++;
                    else if (c.Value == "-" && kept != null && (kept.FunctionMatch == "function" || kept.FunctionMatch == "name") && kept.Fit != "differs")
                        row.Acceptable++;
                    else if (threshold == 0 && wrongLines != null)
                        wrongLines.Add(d.Confidence.ToString(CultureInfo.InvariantCulture) + "% " + d.Plane + " '" + d.Title + "': proposed " + proposed + ", right " + c.Value + " | " +
                                       string.Join(" / ", d.Places.Select(p => p.Relation + " [" + p.Fit + "," + p.FunctionMatch + (p.Position ? ",pos" : string.Empty) + "," + p.Score.ToString(CultureInfo.InvariantCulture) + "]")));
                }
                row.Precision = row.Proposals == 0 ? 1.0 : Math.Round((double)row.Correct / row.Proposals, 3);
                row.PrecisionAcceptable = row.Proposals == 0 ? 1.0 : Math.Round((double)(row.Correct + row.Acceptable) / row.Proposals, 3);
                rows.Add(row);
            }
            return rows;
        }
    }
}
