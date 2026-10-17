using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // The relation editor's chat (engine relations chat <relation> "<message>"): one sentence changes one relation -
    // "add flare release for all jets", "remove the helicopters", "also War Thunder", "only DCS".
    //   1. JoyPro numbers what is in the relation now (N ids, one per command and aircraft) and what could be added
    //      (C ids): the offline rules of "Group by description" (functions and aircraft named in the message, commands
    //      from the manual-checked catalog), the relation's own suggestions and a word search over every game.
    //   2. The offline rules alone propose adds and removes. With the optional local model on, the model picks ids from
    //      these lists only (a GBNF grammar allows nothing else); JoyPro checks every id and rejects the rest.
    //   3. The result is a diff (adds, removes, rejected ids). Nothing changes unless --apply (or the GUI's Apply) asks.
    internal static class EngineRelationChat
    {
        internal sealed class Options
        {
            public string Relation = string.Empty;
            public string Message = string.Empty;
            public string LlmMode = string.Empty;
            public bool ShowPrompt;
            public bool Apply;
        }

        private sealed class Line
        {
            public string Ref = string.Empty;
            public string Game = string.Empty;
            public string Id = string.Empty;
            public string Plane = string.Empty;
            public string Title = string.Empty;
            public string Tags = string.Empty;
            public string Source = string.Empty;
            public bool InScope;
            public bool Rule;
            public List<string> OtherRelations = new List<string>();
            public List<string> Slots = new List<string>();
        }

        private static readonly string[] RemoveWords = { "remove", "delete", "drop", "unlink", "kick", "take out", "get rid", "strip" };
        private static readonly string[] OnlyWords = { "only", "just", "keep only" };
        private static readonly HashSet<string> ChatWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add", "adds", "also", "too", "please", "include", "put", "remove", "delete", "drop", "unlink", "kick", "take", "get", "rid",
            "strip", "only", "just", "keep", "me", "them", "it", "this", "relation", "here"
        };

        public static EngineRelationChatResult Run(Options options)
        {
            EngineRelationChatResult result = new EngineRelationChatResult { Relation = options.Relation ?? string.Empty, Message = (options.Message ?? string.Empty).Trim() };
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(result.Relation, out relation) || relation == null)
            {
                result.Errors.Add("Relation not found: " + result.Relation);
                return result;
            }
            if (result.Message.Length == 0)
            {
                result.Errors.Add("Say what to change, e.g. \"add flare release for all jets\", \"remove the helicopters\", \"also War Thunder\".");
                return result;
            }

            List<string> errors = new List<string>();
            EngineNlGroup.Vocabulary v;
            EngineNlGroup.Catalog catalog;
            bool haveRules = EngineNlGroup.TryLoad(errors, out v, out catalog);
            result.Warnings.AddRange(errors);
            List<EngineRelationSuggest.Command> index = EngineRelationSuggest.Index(result.Warnings);
            Dictionary<string, EngineRelationSuggest.Command> byKey = new Dictionary<string, EngineRelationSuggest.Command>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineRelationSuggest.Command command in index)
            {
                string key = command.Game + "|" + command.Id + "|" + command.Plane;
                if (!byKey.ContainsKey(key))
                    byKey[key] = command;
            }

            // What the message says: remove / keep only / add (the default), which functions, which aircraft.
            string lower = " " + string.Join(" ", EngineNlGroup.Tokenize(result.Message)) + " ";
            string intent = RemoveWords.Any(w => lower.Contains(" " + w + " ")) ? "remove" : OnlyWords.Any(w => lower.Contains(" " + w + " ")) ? "only" : "add";
            result.Intent = intent;
            // The verbs are the chat's, not the grouping rules' ("add" would be looked up in the catalog's descriptions).
            string ruleText = string.Join(" ", result.Message.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !ChatWords.Contains(w.Trim(',', '.', '!', '?', ';', ':').ToLowerInvariant())));
            EngineNlGroup.Understanding u = haveRules && ruleText.Length > 0 ? EngineNlGroup.Parse(ruleText, v, catalog) : new EngineNlGroup.Understanding();
            // Functions found only by the catalog's word search are no reliable reading of a chat message: the relation's
            // own function stands (the local model can still pick other candidates).
            if (u.UsedRetrieval)
                u.Selections.Clear();
            Dictionary<string, EngineNlGroup.ScopeHit> scope = haveRules ? EngineNlGroup.ResolveClauses(v, catalog, u.Include, false) : new Dictionary<string, EngineNlGroup.ScopeHit>();
            Dictionary<string, EngineNlGroup.ScopeHit> excluded = haveRules ? EngineNlGroup.ResolveClauses(v, catalog, u.Exclude, false) : new Dictionary<string, EngineNlGroup.ScopeHit>();
            bool scopeNamed = u.Include.Count > 0;
            foreach (string key in excluded.Keys)
                scope.Remove(key);
            List<string> namedSlots = u.Selections.SelectMany(s => s.Slots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // The relation's meaning: its suggestions know the functions (and give the candidates of step 2).
            EngineRelationSuggestResult suggest = EngineRelationSuggest.Suggest(relation.NAME, "low", 0);
            List<string> relationSlots = suggest.Functions.Where(f => f.Weight >= 0.5).Select(f => f.Slot).ToList();
            List<string> addSlots = namedSlots.Count > 0 ? namedSlots : relationSlots;
            Func<string, bool> inScope = plane => (!scopeNamed || scope.ContainsKey(plane)) && !excluded.ContainsKey(plane);
            // Games whose binds are shared by all their aircraft fit a class only partly ("jets": Arma also flies
            // helicopters); the rules leave them to the user (they stay candidates).
            Func<string, bool> fullyInScope = plane =>
            {
                EngineNlGroup.ScopeHit hit;
                return inScope(plane) && (!scopeNamed || (scope.TryGetValue(plane, out hit) && hit.Quality >= 0.99));
            };
            string scopeLabel = haveRules ? EngineNlGroup.ScopeLabel(u) : string.Empty;
            List<string> understood = new List<string> { intent == "remove" ? "remove" : intent == "only" ? "keep only" : "add" };
            if (namedSlots.Count > 0)
                understood.Add(string.Join(", ", namedSlots.Select(s => SlotName(catalog, s))));
            else if (intent == "add")
                understood.Add("this relation's function");
            if (scopeLabel.Length > 0)
                understood.Add((intent == "add" ? "for " : string.Empty) + scopeLabel + " (" + scope.Count.ToString(CultureInfo.InvariantCulture) + " aircraft/games)");
            else if (intent == "add")
                understood.Add("for all aircraft and games");
            result.Understood = string.Join(" ", understood);
            result.NotUnderstood.AddRange(u.NotUnderstood.Where(w => !RemoveWords.Contains(w) && !OnlyWords.Contains(w) && w != "add" && w != "also"));

            // N lines: what is in the relation now, one per command and aircraft (switched on).
            List<Line> nodes = new List<Line>();
            HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                IEnumerable<string> planes = game == "DCS"
                    ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key)
                    : ((node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(a => a)) ? new[] { game } : new string[0]);
                foreach (string plane in planes)
                {
                    active.Add(game + "|" + node.ID + "|" + plane);
                    EngineRelationSuggest.Command command;
                    byKey.TryGetValue(game + "|" + node.ID + "|" + plane, out command);
                    Line line = new Line
                    {
                        Game = game, Id = node.ID, Plane = plane, Title = command != null ? command.Title : node.ID,
                        Tags = Tags(v, catalog, plane), InScope = scope.ContainsKey(plane), Source = "relation"
                    };
                    if (haveRules)
                        line.Slots = catalog.Rows.Where(r => EngineRelationSuggest.GameKey(r.Game) == game && r.Action == node.ID && (game != "DCS" || r.Plane == plane)).Select(r => r.Slot).Distinct().ToList();
                    nodes.Add(line);
                }
            }

            // C lines: (1) the rules - the asked functions in the named aircraft, (2) the relation's suggestions, (3) a word search.
            List<Line> candidates = new List<Line>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> users = Users(relation.NAME);
            Action<string, string, string, string, bool> addCandidate = (game, id, plane, source, rule) =>
            {
                string key = game + "|" + id + "|" + plane;
                EngineRelationSuggest.Command command;
                if (active.Contains(key) || !seen.Add(key) || !byKey.TryGetValue(key, out command) || command.IsAxis != relation.ISAXIS)
                    return;
                List<string> others;
                candidates.Add(new Line
                {
                    Game = game, Id = id, Plane = plane, Title = command.Title, Tags = Tags(v, catalog, plane), Source = source, Rule = rule,
                    InScope = inScope(plane), OtherRelations = users.TryGetValue(key, out others) ? others : new List<string>()
                });
            };
            if (haveRules && intent != "remove")
            {
                foreach (string slot in addSlots)
                {
                    foreach (EngineAutoGroup.Row row in catalog.Rows.Where(r => string.Equals(r.Slot, slot, StringComparison.OrdinalIgnoreCase) &&
                                                                                !string.Equals(r.Status, "rejected", StringComparison.OrdinalIgnoreCase) &&
                                                                                !string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
                    {
                        string game = EngineRelationSuggest.GameKey(row.Game);
                        string plane = game == "DCS" ? row.Plane : game;
                        if (inScope(plane))
                            addCandidate(game, row.Action, plane, "rules: " + SlotName(catalog, slot), fullyInScope(plane));
                    }
                }
            }
            foreach (EngineRelationSuggestion suggestion in suggest.Suggestions)
            {
                foreach (string plane in suggestion.Planes)
                    addCandidate(suggestion.Game, suggestion.Id, plane, "suggestion (" + suggestion.Confidence + ")", false);
            }
            string searchText = string.Join(" ", EngineRelationSearch.Terms(result.Message).Where(t => !RemoveWords.Contains(t) && !OnlyWords.Contains(t) && t != "add" && t != "also" && t != "all"));
            if (searchText.Length > 0)
            {
                EngineRelationSearchResult search = EngineRelationSearch.Search(searchText, null, null, relation.ISAXIS, 15);
                foreach (EngineRelationSearchHit hit in search.Results)
                {
                    foreach (string plane in hit.Planes.Take(12))
                        addCandidate(hit.Game, hit.Id, plane, "search", false);
                }
            }
            // The likeliest first: the rules' picks, then what fits the named aircraft, then the rest.
            candidates = candidates.OrderByDescending(c => c.Rule).ThenByDescending(c => c.InScope).ThenBy(c => c.Source.StartsWith("suggestion (high", StringComparison.Ordinal) ? 0 : 1).ToList();
            for (int i = 0; i < nodes.Count; i++)
                nodes[i].Ref = "N" + (i + 1).ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < candidates.Count; i++)
                candidates[i].Ref = "C" + (i + 1).ToString(CultureInfo.InvariantCulture);
            result.NodeCount = nodes.Count;
            result.CandidateCount = candidates.Count;

            // The offline rules' answer.
            List<Line> adds = new List<Line>();
            List<Line> removes = new List<Line>();
            if (intent == "add")
            {
                adds.AddRange(candidates.Where(c => c.Rule));
                // "also War Thunder": no function named, the relation's suggestions for the named aircraft.
                if (adds.Count == 0 && scopeNamed)
                    adds.AddRange(candidates.Where(c => c.InScope && c.Source.StartsWith("suggestion (", StringComparison.Ordinal) && !c.Source.Contains("(low)")));
            }
            else if (intent == "remove")
            {
                removes.AddRange(nodes.Where(n => (scopeNamed ? n.InScope : namedSlots.Count > 0) && (namedSlots.Count == 0 || n.Slots.Any(s => namedSlots.Contains(s, StringComparer.OrdinalIgnoreCase)))));
            }
            else if (scopeNamed)
            {
                removes.AddRange(nodes.Where(n => !n.InScope));
            }
            result.Method = "offline rules";
            result.Note = adds.Count + removes.Count == 0 ? "The offline rules found nothing to change." : string.Empty;

            // The local model, when the user switched it on: it re-picks from the numbered lists.
            string mode = (options.LlmMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode.Length == 0)
                mode = EngineLlm.LoadSettings().Enabled ? "on" : "off";
            result.Llm.Mode = mode;
            bool ask = mode == "on" || (mode == "auto" && (adds.Count + removes.Count == 0 || u.NotUnderstood.Count > 0));
            if (options.ShowPrompt || ask)
            {
                EngineLlm.Catalog llmCatalog = EngineLlm.LoadCatalog();
                EngineLlm.ModelInfo model;
                string problem;
                bool usable = EngineLlm.TryGetUsableModel(out model, out problem);
                if (!usable)
                {
                    if (ask && mode == "on" && !options.ShowPrompt)
                    {
                        result.Errors.Add(problem);
                        return result;
                    }
                    if (ask)
                        result.Llm.Note = problem;
                    model = options.ShowPrompt
                        ? EngineLlm.SelectedModel(llmCatalog, EngineLlm.LoadSettings(), EngineLlm.Recommend(EngineLlm.DetectHardware(), llmCatalog)) ?? llmCatalog.Models.LastOrDefault()
                        : null;
                }
                if (model != null)
                {
                    List<Line> shownNodes;
                    List<Line> shownCandidates;
                    int tokens;
                    List<EngineLlm.Message> messages = BuildMessages(model, llmCatalog, relation, result, suggest, nodes, candidates, adds, removes, out shownNodes, out shownCandidates, out tokens);
                    string grammar = BuildGrammar(shownNodes, shownCandidates);
                    result.Llm.PromptTokens = tokens;
                    result.Llm.FunctionCandidates = shownCandidates.Count;
                    result.Llm.ScopeCandidates = shownNodes.Count;
                    if (options.ShowPrompt)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Model: " + model.Id + " (context " + model.ContextSize.ToString(CultureInfo.InvariantCulture) + ", about " + tokens.ToString(CultureInfo.InvariantCulture) + " prompt tokens, " +
                                      shownNodes.Count.ToString(CultureInfo.InvariantCulture) + " N lines, " + shownCandidates.Count.ToString(CultureInfo.InvariantCulture) + " C lines)");
                        foreach (EngineLlm.Message message in messages)
                        {
                            sb.AppendLine("=== " + message.Role);
                            sb.AppendLine(message.Content);
                        }
                        sb.AppendLine("=== grammar");
                        sb.Append(grammar);
                        result.Llm.Prompt = sb.ToString();
                    }
                    if (ask && usable)
                    {
                        EngineLlm.CallResult call = EngineLlm.Complete(model, messages, grammar);
                        result.Llm.Used = true;
                        result.Llm.Model = model.Id;
                        result.Llm.ProcessId = call.ProcessId;
                        result.Llm.ProcessExited = call.ProcessExited;
                        result.Llm.DurationMs = call.DurationMs;
                        result.Llm.Answer = call.Content ?? string.Empty;
                        if (!string.IsNullOrEmpty(call.Error))
                        {
                            result.Warnings.Add("Local model failed (" + call.Error + "); using the offline rules.");
                        }
                        else
                        {
                            List<Line> modelAdds;
                            List<Line> modelRemoves;
                            string note;
                            if (Validate(call.Content, shownNodes, shownCandidates, result, out modelAdds, out modelRemoves, out note))
                            {
                                adds = modelAdds;
                                removes = modelRemoves;
                                result.Note = note;
                                result.Llm.Note = note;
                                result.Method = "local model (" + model.Id + ") on JoyPro's candidates";
                            }
                        }
                    }
                }
            }

            result.Add.AddRange(Group(adds));
            result.Remove.AddRange(Group(removes));
            if (options.Apply && result.Add.Count + result.Remove.Count > 0)
            {
                EngineRelationSuggestApplyResult applied = EngineRelationSuggest.Apply(relation.NAME, result.Add.Select(c => c.Spec).ToList(), new List<string>(), result.Remove.Select(c => c.Spec).ToList(), new List<string>());
                result.Applied = true;
                result.Added = applied.Added;
                result.Removed = applied.Removed;
                result.Errors.AddRange(applied.Errors);
            }
            return result;
        }

        // One change per game command: every aircraft of it in one spec (game:id:plane1|plane2).
        private static List<EngineRelationChatChange> Group(List<Line> lines)
        {
            return lines.GroupBy(l => l.Game + "|" + l.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EngineRelationChatChange
                {
                    Game = g.First().Game,
                    Id = g.First().Id,
                    Title = g.First().Title,
                    Planes = g.Select(l => l.Plane).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                    Refs = g.Select(l => l.Ref).ToList(),
                    Source = g.First().Source,
                    InOtherRelations = g.SelectMany(l => l.OtherRelations).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();
        }

        private static string SlotName(EngineNlGroup.Catalog catalog, string slot)
        {
            EngineAutoGroup.Slot info;
            return catalog != null && catalog.Slots.TryGetValue(slot, out info) ? info.Name : slot;
        }

        // "jet, fighter, modern" - what the model needs for "remove the helicopters" or "all WW2 aircraft".
        private static string Tags(EngineNlGroup.Vocabulary v, EngineNlGroup.Catalog catalog, string plane)
        {
            if (catalog == null)
                return string.Empty;
            EngineAutoGroup.Aircraft info;
            if (!catalog.Aircraft.TryGetValue(plane, out info))
                return string.Empty;
            List<string> tags = new List<string>();
            if (!EngineAutoGroup.IsDcs(info.Game))
                tags.Add("game");
            tags.Add(info.Type == "rotary_wing" ? "helicopter" : info.Type == "fixed_wing" ? "fixed wing" : info.Type.Replace('_', ' '));
            HashSet<string> extra;
            string baseName = info.VariantOf.Length > 0 ? info.VariantOf : plane;
            if (v != null && v.Tags.TryGetValue(baseName, out extra))
                tags.AddRange(extra.OrderBy(t => t, StringComparer.Ordinal).Take(4));
            string role = EngineNlGroup.LookupRole(plane);
            if (role.Length > 0)
                tags.Add(role.Replace('_', ' '));
            if (info.Era.Length > 0)
                tags.Add(info.Era.Replace('_', ' '));
            return string.Join(", ", tags.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static Dictionary<string, List<string>> Users(string self)
        {
            Dictionary<string, List<string>> users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null && r.NAME != self))
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null && n.AIRCRAFT != null))
                {
                    string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.Where(p => p.Value))
                    {
                        string key = game + "|" + node.ID + "|" + (game == "DCS" ? plane.Key : game);
                        List<string> list;
                        if (!users.TryGetValue(key, out list))
                            users[key] = list = new List<string>();
                        if (!list.Contains(relation.NAME))
                            list.Add(relation.NAME);
                    }
                }
            }
            return users;
        }

        // The user turn: the relation, the message (twice), the rules' reading and the numbered lists. Shrunk until it
        // fits the model's context minus the answer: fewer C lines, fewer few-shot turns, fewer N lines.
        private static List<EngineLlm.Message> BuildMessages(EngineLlm.ModelInfo model, EngineLlm.Catalog llmCatalog, Relation relation, EngineRelationChatResult result,
            EngineRelationSuggestResult suggest, List<Line> nodes, List<Line> candidates, List<Line> ruleAdds, List<Line> ruleRemoves,
            out List<Line> shownNodes, out List<Line> shownCandidates, out int tokens)
        {
            int maxCandidates = Math.Max(20, model.MaxScopes);
            int maxNodes = Math.Max(20, model.MaxScopes);
            int fewShot = Math.Max(0, model.FewShot);
            int budget = Math.Max(512, model.ContextSize - model.MaxTokens - 64);
            List<EngineLlm.Message> messages = null;
            shownNodes = nodes;
            shownCandidates = candidates;
            tokens = 0;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                shownNodes = nodes.Take(maxNodes).ToList();
                shownCandidates = candidates.Take(maxCandidates).ToList();
                messages = EngineLlm.ChatConversation(llmCatalog, fewShot, UserPrompt(relation, result, suggest, shownNodes, shownCandidates, ruleAdds, ruleRemoves));
                tokens = EngineLlm.EstimateTokens(messages);
                if (tokens <= budget)
                    break;
                if (maxCandidates > 40)
                    maxCandidates = Math.Max(40, maxCandidates * 3 / 4);
                else if (fewShot > 1)
                    fewShot--;
                else if (maxNodes > 30)
                    maxNodes = Math.Max(30, maxNodes * 3 / 4);
                else if (fewShot > 0)
                    fewShot = 0;
                else if (maxCandidates > 12)
                    maxCandidates = Math.Max(12, maxCandidates * 3 / 4);
                else if (maxNodes > 12)
                    maxNodes = Math.Max(12, maxNodes * 3 / 4);
                else
                    break;
            }
            return messages;
        }

        private static string UserPrompt(Relation relation, EngineRelationChatResult result, EngineRelationSuggestResult suggest, List<Line> nodes, List<Line> candidates, List<Line> ruleAdds, List<Line> ruleRemoves)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Message: \"" + result.Message + "\"");
            string function = suggest.Functions.Count > 0 ? string.Join(", ", suggest.Functions.Where(f => f.Weight >= 0.5).Select(f => f.Name)) : "(not in the function catalog)";
            sb.AppendLine("Relation: \"" + relation.NAME + "\" (" + (relation.ISAXIS ? "axis" : "button") + "; function: " + function + ")");
            List<string> ruleAddRefs = ruleAdds.Select(a => a.Ref).Where(r => candidates.Any(c => c.Ref == r)).ToList();
            List<string> ruleRemoveRefs = ruleRemoves.Select(a => a.Ref).Where(r => nodes.Any(n => n.Ref == r)).ToList();
            sb.AppendLine("Rule-based reading (may be incomplete or wrong): " + result.Understood +
                          (result.NotUnderstood.Count > 0 ? "; words not understood: " + string.Join(", ", result.NotUnderstood) : string.Empty) +
                          "; rules would add " + (ruleAddRefs.Count > 0 ? string.Join(",", ruleAddRefs) : "nothing") + " and remove " + (ruleRemoveRefs.Count > 0 ? string.Join(",", ruleRemoveRefs) : "nothing"));
            sb.AppendLine();
            sb.AppendLine("In the relation now (N ids, only for remove):");
            foreach (Line n in nodes)
                sb.AppendLine(n.Ref + " " + Label(n) + " \"" + n.Title + "\"" + (n.Tags.Length > 0 ? " [" + n.Tags + "]" : string.Empty));
            if (nodes.Count == 0)
                sb.AppendLine("(empty)");
            sb.AppendLine();
            sb.AppendLine("Candidates to add (C ids, only for add):");
            foreach (Line c in candidates)
                sb.AppendLine(c.Ref + " " + Label(c) + " \"" + c.Title + "\" [" + (c.Tags.Length > 0 ? c.Tags + "; " : string.Empty) + c.Source +
                              (c.OtherRelations.Count > 0 ? "; already in '" + string.Join("', '", c.OtherRelations) + "'" : string.Empty) + "]");
            if (candidates.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();
            sb.Append("Message again: \"" + result.Message + "\"");
            return sb.ToString();
        }

        private static string Label(Line line)
        {
            return line.Game == "DCS" ? "DCS " + line.Plane : line.Game;
        }

        // GBNF: {"note":"...","add":[C ids],"remove":[N ids]}, nothing else.
        private static string BuildGrammar(List<Line> nodes, List<Line> candidates)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("root ::= \"{\" ws \"\\\"note\\\":\" ws note \",\" ws \"\\\"add\\\":\" ws adds \",\" ws \"\\\"remove\\\":\" ws removes ws \"}\"");
            sb.AppendLine(candidates.Count == 0 ? "adds ::= \"[\" ws \"]\"" : "adds ::= \"[\" ws \"]\" | \"[\" ws cid (ws \",\" ws cid)* ws \"]\"");
            sb.AppendLine(nodes.Count == 0 ? "removes ::= \"[\" ws \"]\"" : "removes ::= \"[\" ws \"]\" | \"[\" ws nid (ws \",\" ws nid)* ws \"]\"");
            if (candidates.Count > 0)
                sb.AppendLine("cid ::= " + string.Join(" | ", candidates.Select(c => "\"\\\"" + c.Ref + "\\\"\"")));
            if (nodes.Count > 0)
                sb.AppendLine("nid ::= " + string.Join(" | ", nodes.Select(n => "\"\\\"" + n.Ref + "\\\"\"")));
            sb.AppendLine("note ::= \"\\\"\" [^\"\\\\\\n]{0,160} \"\\\"\"");
            sb.AppendLine("ws ::= [ \\t\\n]{0,4}");
            return sb.ToString();
        }

        // Every id must be one of the lists the model saw: C ids only for add, N ids only for remove. Anything else is
        // rejected (and listed). Invalid JSON keeps the offline rules.
        private static bool Validate(string content, List<Line> nodes, List<Line> candidates, EngineRelationChatResult result, out List<Line> adds, out List<Line> removes, out string note)
        {
            adds = new List<Line>();
            removes = new List<Line>();
            note = string.Empty;
            Dictionary<string, object> answer;
            try
            {
                answer = EngineSerialization.FromJson<Dictionary<string, object>>(EngineNlGroup.ExtractJsonObject(content ?? string.Empty));
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Local model answer is not valid JSON (" + ex.Message + "); using the offline rules.");
                return false;
            }
            if (answer == null)
            {
                result.Warnings.Add("Local model gave no answer; using the offline rules.");
                return false;
            }
            note = EngineNlGroup.Text(answer, "note");
            foreach (string id in EngineNlGroup.Strings(EngineNlGroup.Get(answer, "add")))
            {
                Line line = candidates.FirstOrDefault(c => string.Equals(c.Ref, id, StringComparison.Ordinal));
                if (line == null)
                {
                    result.Rejected.Add(id);
                    continue;
                }
                if (!adds.Contains(line))
                    adds.Add(line);
            }
            foreach (string id in EngineNlGroup.Strings(EngineNlGroup.Get(answer, "remove")))
            {
                Line line = nodes.FirstOrDefault(n => string.Equals(n.Ref, id, StringComparison.Ordinal));
                if (line == null)
                {
                    result.Rejected.Add(id);
                    continue;
                }
                if (!removes.Contains(line))
                    removes.Add(line);
            }
            if (result.Rejected.Count > 0)
                result.Warnings.Add("Rejected ids the local model made up or put in the wrong list: " + string.Join(", ", result.Rejected) + ".");
            return true;
        }

        public static string ToText(EngineRelationChatResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Relation: " + result.Relation);
            sb.AppendLine("Message: " + result.Message);
            sb.AppendLine("Understood: " + result.Understood);
            sb.AppendLine("Method: " + result.Method + (result.Llm.Used ? " (" + result.Llm.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms)" : string.Empty));
            if (result.Note.Length > 0)
                sb.AppendLine("Note: " + result.Note);
            if (result.NotUnderstood.Count > 0)
                sb.AppendLine("Not understood: " + string.Join(", ", result.NotUnderstood));
            sb.AppendLine("Candidates: " + result.CandidateCount.ToString(CultureInfo.InvariantCulture) + " to add, " + result.NodeCount.ToString(CultureInfo.InvariantCulture) + " in the relation");
            sb.AppendLine("Add: " + result.Add.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineRelationChatChange change in result.Add)
                sb.AppendLine("  + " + change.Spec + " | " + change.Title + (change.InOtherRelations.Count > 0 ? " | also in " + string.Join(", ", change.InOtherRelations) : string.Empty));
            sb.AppendLine("Remove: " + result.Remove.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineRelationChatChange change in result.Remove)
                sb.AppendLine("  - " + change.Spec + " | " + change.Title);
            if (result.Rejected.Count > 0)
                sb.AppendLine("Rejected: " + string.Join(", ", result.Rejected));
            if (result.Applied)
                sb.AppendLine("Applied: " + result.Added.ToString(CultureInfo.InvariantCulture) + " added, " + result.Removed.ToString(CultureInfo.InvariantCulture) + " removed");
            if (result.Llm.Prompt.Length > 0)
                sb.AppendLine(result.Llm.Prompt);
            foreach (string warning in result.Warnings.Distinct().Where(w => !w.Contains("action catalog not found")).Take(10))
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    public sealed class EngineRelationChatChange
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public List<string> Planes { get; set; }
        public List<string> Refs { get; set; }
        public string Source { get; set; }
        public List<string> InOtherRelations { get; set; }

        public string Spec
        {
            get { return Game + ":" + Id + ":" + string.Join("|", Planes); }
        }

        public EngineRelationChatChange()
        {
            Game = Id = Title = Source = string.Empty;
            Planes = new List<string>();
            Refs = new List<string>();
            InOtherRelations = new List<string>();
        }
    }

    public sealed class EngineRelationChatResult
    {
        public string Relation { get; set; }
        public string Message { get; set; }
        public string Intent { get; set; }
        public string Understood { get; set; }
        public List<string> NotUnderstood { get; set; }
        public string Method { get; set; }
        public string Note { get; set; }
        public int NodeCount { get; set; }
        public int CandidateCount { get; set; }
        public List<EngineRelationChatChange> Add { get; set; }
        public List<EngineRelationChatChange> Remove { get; set; }
        public List<string> Rejected { get; set; }
        public EngineNlLlmInfo Llm { get; set; }
        public bool Applied { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationChatResult()
        {
            Relation = Message = Intent = Understood = Method = Note = string.Empty;
            NotUnderstood = new List<string>();
            Add = new List<EngineRelationChatChange>();
            Remove = new List<EngineRelationChatChange>();
            Rejected = new List<string>();
            Llm = new EngineNlLlmInfo();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
