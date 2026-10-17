using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // The JoyPro assistant of the Relations page (engine relations assistant "<message>" [--relation <name>]
    // [--llm auto|on|off] [--max-steps N] [--show-prompt]). It changes relations directly; the GUI shows the result at
    // once and undoes the whole turn with one Undo (one engine command = one undo snapshot).
    //
    //   With the local AI model (EngineLlm, the user's consent and download or their own llama.cpp): the model works
    //   through tools - list_relations, show_relation, resolve, search, suggest, create_relation, rename_relation,
    //   add_to_relation, remove_from_relation, done. Every answer is one JSON tool call (a GBNF grammar allows nothing
    //   else); JoyPro runs it on the profile and answers with a compact RESULT text, until the model calls done or the
    //   step limit is reached. The model process lives for the whole turn (one load) and is killed afterwards.
    //   JoyPro checks every id: only ids of this turn's RESULT lines count, an action of the wrong kind (button into an
    //   axis relation) is skipped, nothing is deleted.
    //
    //   Without the model, the same box understands simple commands with the offline brain (resolve): "Throttle ALL:
    //   all throttle axes" (create and fill), "add <what> [to <relation>]", "remove <what> [from <relation>]",
    //   "rename <relation> to <name>". Anything else says that it needs the model.
    internal static class EngineRelationAssistant
    {
        internal sealed class Options
        {
            public string Message = string.Empty;
            public string Relation = string.Empty;
            public string LlmMode = string.Empty;
            public int MaxSteps = 16;
            public bool ShowPrompt;
        }

        // One action of a game a RESULT line showed: one command (and title) with every aircraft that has it.
        internal sealed class ActionRef
        {
            public string Ref = string.Empty;
            public string Game = string.Empty;
            public string Id = string.Empty;
            public string Title = string.Empty;
            public bool IsAxis;
            public bool KindKnown = true;
            public readonly SortedSet<string> Planes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> InRelations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // DCS: the aircraft where the same id means something else (not in Planes, never added through this id).
            public int SameIdDifferent;
            public string MeaningNote = string.Empty;

            public string Spec(IEnumerable<string> planes)
            {
                return Game + ":" + Id + ":" + string.Join("|", planes);
            }
        }

        internal sealed class ToolCall
        {
            public string Tool = string.Empty;
            public string Query = string.Empty;
            public string Kind = string.Empty;
            public List<string> Games = new List<string>();
            public string Relation = string.Empty;
            public string Name = string.Empty;
            public string NewName = string.Empty;
            public List<string> Ids = new List<string>();
            public string Reply = string.Empty;

            public string Describe()
            {
                switch (Tool)
                {
                    case "search":
                    case "resolve":
                        return Tool + "(\"" + Query + "\"" + (Kind.Length > 0 ? ", " + Kind : string.Empty) + (Games.Count > 0 ? ", " + string.Join("|", Games) : string.Empty) + ")";
                    case "suggest":
                    case "show_relation":
                        return Tool + "(\"" + Relation + "\")";
                    case "create_relation":
                        return Tool + "(\"" + Name + "\", " + Kind + ")";
                    case "rename_relation":
                        return Tool + "(\"" + Relation + "\" -> \"" + NewName + "\")";
                    case "add_to_relation":
                    case "remove_from_relation":
                        return Tool + "(\"" + Relation + "\", " + string.Join(",", Ids) + ")";
                    default:
                        return Tool + "()";
                }
            }
        }

        public static readonly string[] Tools =
        {
            "list_relations", "show_relation", "resolve", "search", "suggest", "create_relation", "rename_relation", "add_to_relation", "remove_from_relation", "done"
        };

        // ------------------------------------------------------------------------------------------------------
        // The turn
        // ------------------------------------------------------------------------------------------------------

        public static EngineRelationAssistantResult Run(Options options)
        {
            EngineRelationAssistantResult result = new EngineRelationAssistantResult { Message = (options.Message ?? string.Empty).Trim() };
            Toolbox box = new Toolbox(result);
            if (!string.IsNullOrWhiteSpace(options.Relation))
            {
                string focus = box.FindRelation(options.Relation);
                if (focus == null)
                {
                    result.Errors.Add("Relation not found: " + options.Relation);
                    return result;
                }
                result.FocusRelation = focus;
            }
            if (result.Message.Length == 0)
            {
                result.Errors.Add("Say what to do, e.g. \"Throttle ALL: all throttle axes\" or \"add the collectives to Throttle\".");
                return result;
            }

            string mode = (options.LlmMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode.Length == 0)
                mode = "auto";
            result.Llm.Mode = mode;
            EngineLlm.ModelInfo model = null;
            string problem = string.Empty;
            bool usable = mode != "off" && EngineLlm.TryGetUsableModel(out model, out problem);
            if (options.ShowPrompt)
            {
                EngineLlm.Catalog catalog = EngineLlm.LoadCatalog();
                List<EngineLlm.Message> messages = Conversation(catalog, UserTurn(result.Message, box, result.FocusRelation));
                StringBuilder sb = new StringBuilder();
                foreach (EngineLlm.Message message in messages)
                    sb.AppendLine("=== " + message.Role).AppendLine(message.Content);
                sb.AppendLine("=== grammar").Append(BuildGrammar());
                result.Llm.Prompt = sb.ToString();
                result.Llm.Note = usable ? "The local AI model would answer (" + model.Id + ")." : problem;
                return result;
            }
            if (mode == "on" && !usable)
            {
                result.Errors.Add(problem.Length > 0 ? problem : "The local AI model is off.");
                return result;
            }
            if (usable)
            {
                RunModel(model, options, box, result);
            }
            else
            {
                result.Llm.Note = problem;
                RunOffline(box, result);
            }
            box.Finish();
            return result;
        }

        // The tool loop with the local model.
        private static void RunModel(EngineLlm.ModelInfo model, Options options, Toolbox box, EngineRelationAssistantResult result)
        {
            EngineLlm.Catalog catalog = EngineLlm.LoadCatalog();
            List<EngineLlm.Message> messages = Conversation(catalog, UserTurn(result.Message, box, result.FocusRelation));
            int contextSize = model.Local ? Math.Max(model.ContextSize, 16384) : Math.Max(model.ContextSize, string.Equals(model.Tier, "large", StringComparison.OrdinalIgnoreCase) ? 12288 : model.ContextSize);
            // The user's own model may be large (a 30B GGUF partly on the CPU loads for minutes); one load per turn.
            if (model.Local)
                model.LoadTimeoutSeconds = Math.Max(model.LoadTimeoutSeconds, 900);
            // Room for one call with many ids (about 6 tokens each).
            int answerTokens = contextSize >= 12000 ? 1100 : 420;
            // How many lines a result shows: a big context sees whole results (the set id still covers everything).
            box.MaxLines = contextSize >= 16000 ? 140 : contextSize >= 12000 ? 80 : 45;
            string grammar = BuildGrammar();
            int maxSteps = Math.Max(1, Math.Min(40, options.MaxSteps));
            result.Method = "local AI model (" + model.Id + ")";
            result.Llm.Used = true;
            result.Llm.Model = model.Id;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            EngineProgress.Step(0, maxSteps, "Starting the local AI model");
            EngineLlm.Session session = EngineLlm.Session.Start(model, contextSize);
            try
            {
                result.Llm.ProcessId = session.ProcessId;
                if (session.Error.Length > 0)
                {
                    result.Warnings.Add("The local AI model did not start (" + session.Error + "); JoyPro used its offline brain.");
                    result.Llm.Used = false;
                    RunOffline(box, result);
                    return;
                }
                string lastCall = string.Empty;
                int invalid = 0;
                bool reminded = false;
                List<string> parts = RequestParts(result.Message);
                for (int step = 1; step <= maxSteps; step++)
                {
                    Shrink(messages, contextSize - answerTokens - 256);
                    EngineProgress.Step(step, maxSteps, "The AI is working", step == 1 ? "reading your request" : box.LastActivity);
                    EngineLlm.CallResult call = session.Ask(messages, grammar, answerTokens, true);
                    EngineRelationAssistantStep record = new EngineRelationAssistantStep { Step = step, DurationMs = call.DurationMs, Raw = call.Content ?? string.Empty };
                    result.Steps.Add(record);
                    if (!string.IsNullOrEmpty(call.Error))
                    {
                        record.Error = call.Error;
                        result.Warnings.Add("The local AI model stopped answering (" + call.Error + ").");
                        break;
                    }
                    messages.Add(new EngineLlm.Message { Role = "assistant", Content = (call.Content ?? string.Empty).Trim() });
                    string parseError;
                    ToolCall tool = ParseCall(call.Content, out parseError);
                    if (tool == null)
                    {
                        invalid++;
                        record.Error = parseError;
                        if (invalid >= 3)
                        {
                            result.Warnings.Add("The local AI model gave no usable tool call three times; stopped.");
                            break;
                        }
                        messages.Add(new EngineLlm.Message { Role = "user", Content = "ERROR " + parseError + " Answer with exactly one JSON tool call, e.g. {\"tool\":\"done\",\"reply\":\"...\"}." });
                        continue;
                    }
                    invalid = 0;
                    record.Tool = tool.Tool;
                    record.Call = tool.Describe();
                    if (tool.Tool == "done")
                    {
                        // A part of the request nobody looked up yet ("... including collectives"): once, the model is
                        // sent back to it before the turn may end.
                        List<string> open = parts.Where(p => !box.Covered(p)).ToList();
                        if (open.Count > 0 && !reminded)
                        {
                            reminded = true;
                            record.Error = "parts not looked up: " + string.Join(", ", open);
                            messages.Add(new EngineLlm.Message
                            {
                                Role = "user",
                                Content = "ERROR Not done yet: you did not look up " + string.Join(" and ", open.Select(p => "\"" + p + "\"")) +
                                          ". Resolve or search each, add what fits, then call done."
                            });
                            continue;
                        }
                        result.Reply = tool.Reply.Trim();
                        record.Ok = true;
                        result.Finished = true;
                        break;
                    }
                    string signature = tool.Describe();
                    string answer;
                    if (signature == lastCall)
                    {
                        answer = "ERROR You already called " + signature + ". Do something else or call done.";
                        record.Error = "repeated call";
                    }
                    else
                    {
                        answer = box.Execute(tool, record);
                    }
                    lastCall = signature;
                    messages.Add(new EngineLlm.Message { Role = "user", Content = answer });
                }
                if (!result.Finished && result.Steps.Count >= maxSteps)
                    result.Warnings.Add("The assistant reached its step limit (" + maxSteps.ToString(CultureInfo.InvariantCulture) + "); what it did so far is kept.");
            }
            finally
            {
                session.Dispose();
                result.Llm.ProcessExited = session.ProcessExited;
                watch.Stop();
                result.Llm.DurationMs = watch.ElapsedMilliseconds;
            }
        }

        // Older tool results shrink to their first line when the conversation outgrows the model's context.
        private static void Shrink(List<EngineLlm.Message> messages, int budget)
        {
            for (int i = 1; i < messages.Count - 2 && EngineLlm.EstimateTokens(messages) > budget; i++)
            {
                EngineLlm.Message message = messages[i];
                if (message.Role != "user" || !message.Content.StartsWith("RESULT", StringComparison.Ordinal))
                    continue;
                int newline = message.Content.IndexOf('\n');
                if (newline > 0)
                    message.Content = message.Content.Substring(0, newline) + "\n(the lines were shortened; the ids stay valid)";
            }
        }

        // System prompt and few-shot turns (DB\Llm\models.json "relationAssistant"), then the request.
        private static List<EngineLlm.Message> Conversation(EngineLlm.Catalog catalog, string user)
        {
            List<EngineLlm.Message> messages = new List<EngineLlm.Message>
            {
                new EngineLlm.Message { Role = "system", Content = catalog.AssistantSystemPrompt.Length > 0 ? catalog.AssistantSystemPrompt : DefaultSystemPrompt }
            };
            foreach (KeyValuePair<string, string> turn in catalog.AssistantExamples)
                messages.Add(new EngineLlm.Message { Role = turn.Key, Content = turn.Value });
            messages.Add(new EngineLlm.Message { Role = "user", Content = user });
            return messages;
        }

        private static string UserTurn(string message, Toolbox box, string focus)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("REQUEST: " + message);
            if (!string.IsNullOrEmpty(focus))
                sb.AppendLine("The user has the relation \"" + focus + "\" open (" + box.Brief(focus) + "). \"this relation\" and \"it\" mean that one.");
            List<string> parts = RequestParts(message);
            if (parts.Count > 1)
                sb.AppendLine("The request has " + parts.Count.ToString(CultureInfo.InvariantCulture) + " parts; look up each one: " +
                              string.Join("; ", parts.Select((p, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ") " + p)) + ".");
            int count = InternalDataManagement.AllRelations.Count;
            sb.Append("The profile has " + count.ToString(CultureInfo.InvariantCulture) + " relations.");
            return sb.ToString();
        }

        // "... called Throttle ALL including collectives and spaceship forward/backward thrusters" -> "thrust",
        // "collective", "spaceship forward/backward thruster": the things the request names (empty without such a list).
        internal static List<string> RequestParts(string message)
        {
            List<string> parts = new List<string>();
            string text = (message ?? string.Empty).Trim().TrimEnd('.', '!');
            Match tail = Regex.Match(text, @"^(?<main>.+?)[,;]?\s+(?:including|incl\.?|plus|as well as|together with|and also)\s+(?<extra>.+)$", RegexOptions.IgnoreCase);
            if (!tail.Success)
                return parts;
            string main = tail.Groups["main"].Value;
            Match named = Regex.Match(main, @"^(?:add|put|group|bind|move)?\s*(?<what>.+?)\s+(?:in|into|to)\s+(?:one|a|a new|the)\s+(?:new\s+)?relation\s+(?:called|named)\s+.+$", RegexOptions.IgnoreCase);
            string what = CleanQuery(named.Success ? named.Groups["what"].Value : main);
            if (what.Length > 0 && what.Length <= 60)
                parts.Add(what);
            foreach (string extra in Regex.Split(tail.Groups["extra"].Value, @"\s*(?:,|;|\band\b|&)\s*", RegexOptions.IgnoreCase))
            {
                string part = CleanQuery(extra);
                if (part.Length > 0 && !parts.Contains(part, StringComparer.OrdinalIgnoreCase))
                    parts.Add(part);
            }
            return parts;
        }

        internal const string DefaultSystemPrompt =
            "You are the JoyPro assistant. JoyPro puts the same function of many games and aircraft on one joystick input. A relation is a named list of game actions " +
            "(e.g. DCS F-16C_50 \"Throttle\", IL-2 \"Engines throttle control\", Star Citizen \"Throttle forward/back\") that all go on ONE input; it is an axis relation or a button relation, never both.\n" +
            "You change relations only through tools. Each answer is exactly one JSON tool call. JoyPro runs it and replies with RESULT lines (or ERROR). Then make the next call. When the request is done, call done.\n" +
            "Tools:\n" +
            "{\"tool\":\"list_relations\"} - the relations of the profile.\n" +
            "{\"tool\":\"show_relation\",\"relation\":\"NAME\"} - the actions in a relation, with ids.\n" +
            "{\"tool\":\"resolve\",\"query\":\"WHAT IT DOES\",\"kind\":\"axis|button|any\"} - JoyPro's knowledge of every game's action for a function, with synonyms. Returns a set id S<n> and action ids A<n>. Try it first.\n" +
            "{\"tool\":\"search\",\"query\":\"WORDS\",\"kind\":\"axis|button|any\",\"games\":[\"DCS\"]} - word search in the action titles of every game (games optional). For what resolve misses.\n" +
            "{\"tool\":\"suggest\",\"relation\":\"NAME\"} - what JoyPro's catalog says fits a relation, with the reason.\n" +
            "{\"tool\":\"create_relation\",\"name\":\"NAME\",\"kind\":\"axis|button\"}\n" +
            "{\"tool\":\"rename_relation\",\"relation\":\"NAME\",\"new_name\":\"NAME\"}\n" +
            "{\"tool\":\"add_to_relation\",\"relation\":\"NAME\",\"ids\":[\"A3\",\"S2\"]} - S<n> adds every action of that result.\n" +
            "{\"tool\":\"remove_from_relation\",\"relation\":\"NAME\",\"ids\":[\"A4\"]}\n" +
            "{\"tool\":\"done\",\"reply\":\"ONE OR TWO SENTENCES FOR THE USER\"}\n" +
            "Rules:\n" +
            "1. Use only ids from RESULT lines of this conversation. Never invent ids.\n" +
            "2. Add only actions that do exactly what the user asked. No buttons into an axis relation and no axes into a button relation. Leave out actions that only share a word (trim, friction, cutoff, mixture, propeller, brakes, zoom, lateral or vertical thrust, mining, turret...).\n" +
            "3. Add a whole set S<n> only when every line of it fits; else list the fitting A ids. Check the lines of a set before you add it.\n" +
            "4. Throttle, thrust, power lever, helicopter collective and spaceship forward/backward thrust are axes. A twin-engine aircraft's left and right throttle both belong to an all-throttle relation.\n" +
            "5. Create a relation only when the user asks for a new one or names one that does not exist. Never touch relations the user did not mention. There is no delete.\n" +
            "6. When the request names several things (\"X including Y and Z\"), look up each one, add what fits of each, and only then call done.\n" +
            "7. Be quick: a few resolve or search calls, then add, then done. In done, say in plain words only what you really added.\n" +
            "8. A DCS action id belongs to its aircraft: the same id can mean something else in another aircraft (\"Pitch\" in a jet, \"Collective\" in a helicopter; cockpit switch ids are another switch in every module). " +
            "Each RESULT line is ONE meaning with only the aircraft where the id means that. \"same id means something else in N other aircraft\" means those aircraft were left out on purpose: never add them through that line; look up their own action.";

        // ------------------------------------------------------------------------------------------------------
        // Without the model: simple commands, read by the offline brain
        // ------------------------------------------------------------------------------------------------------

        private static void RunOffline(Toolbox box, EngineRelationAssistantResult result)
        {
            result.Method = "offline brain (no AI model)";
            string text = result.Message.Trim().TrimEnd('.', '!');
            Match m;
            // "rename X to Y" / "rename to Y" (the open relation)
            m = Regex.Match(text, @"^rename\s+(?:(?:the\s+)?relation\s+)?(?:""(?<from>[^""]+)""|'(?<from>[^']+)'|(?<from>.+?))?\s+to\s+(?:""(?<to>[^""]+)""|'(?<to>[^']+)'|(?<to>.+))$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string from = m.Groups["from"].Success && m.Groups["from"].Value.Trim().Length > 0 ? m.Groups["from"].Value.Trim() : result.FocusRelation;
                if (string.IsNullOrEmpty(from) || string.Equals(from, "it", StringComparison.OrdinalIgnoreCase) || string.Equals(from, "this", StringComparison.OrdinalIgnoreCase))
                    from = result.FocusRelation;
                Offline(box, result, new ToolCall { Tool = "rename_relation", Relation = from ?? string.Empty, NewName = m.Groups["to"].Value.Trim() });
                return;
            }
            // "remove <what> [from <relation>]"
            m = Regex.Match(text, @"^(?:remove|delete|drop|take out)\s+(?<what>.+?)(?:\s+from\s+(?:""(?<rel>[^""]+)""|'(?<rel>[^']+)'|(?<rel>.+)))?$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string relation = m.Groups["rel"].Success ? m.Groups["rel"].Value.Trim() : result.FocusRelation;
                OfflineRemove(box, result, relation ?? string.Empty, m.Groups["what"].Value.Trim());
                return;
            }
            // "add <what> [to <relation>]"
            m = Regex.Match(text, @"^(?:add|also add|include|put)\s+(?<what>.+?)(?:\s+(?:to|into|in)\s+(?:""(?<rel>[^""]+)""|'(?<rel>[^']+)'|(?<rel>[^""']+)))?$", RegexOptions.IgnoreCase);
            if (m.Success && (m.Groups["rel"].Success || !string.IsNullOrEmpty(result.FocusRelation)))
            {
                string relation = m.Groups["rel"].Success ? m.Groups["rel"].Value.Trim() : result.FocusRelation;
                string existing = box.FindRelation(relation);
                if (existing != null || !m.Groups["rel"].Success)
                {
                    OfflineAdd(box, result, existing ?? relation, m.Groups["what"].Value.Trim(), false);
                    return;
                }
            }
            // "Name: what" / "create Name with what" / "... into one relation called Name", each with an optional
            // "including A and B" (more functions for the same relation).
            List<string> extras = new List<string>();
            string main = text;
            Match tail = Regex.Match(text, @"^(?<main>.+?)[,;]?\s+(?:including|incl\.?|plus|as well as|together with|and also|with all|also)\s+(?<extra>.+)$", RegexOptions.IgnoreCase);
            if (tail.Success && !text.Contains(":"))
            {
                main = tail.Groups["main"].Value.Trim();
                extras.AddRange(Regex.Split(tail.Groups["extra"].Value, @"\s*(?:,|;|\band\b|&)\s*", RegexOptions.IgnoreCase).Select(e => e.Trim()).Where(e => e.Length > 0));
            }
            m = Regex.Match(main, @"^(?<name>[^:]{1,60}):\s*(?<what>.+)$");
            if (!m.Success)
                m = Regex.Match(main, @"^(?:create|make|new)\s+(?:a\s+)?(?:new\s+)?(?:relation\s+)?(?:called\s+|named\s+)?(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>.+?))\s+(?:with|for|from|of)\s+(?<what>.+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(main, @"^(?:add|put|group|bind|move)?\s*(?<what>.+?)\s+(?:in|into|to)\s+(?:one|a|a new|the)\s+(?:new\s+)?relation\s+(?:called|named)\s+(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>.+))$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string name = m.Groups["name"].Value.Trim().Trim('"', '\'');
                List<string> whats = new List<string> { m.Groups["what"].Value.Trim() };
                whats.AddRange(extras);
                string existing = box.FindRelation(name);
                OfflineAdd(box, result, existing ?? name, whats, existing == null);
                return;
            }
            // Only a function ("all throttle axes") with a relation open: add it there.
            if (!string.IsNullOrEmpty(result.FocusRelation))
            {
                OfflineAdd(box, result, result.FocusRelation, text, false);
                return;
            }
            result.NeedsModel = true;
            result.Reply = "Without the local AI model I understand simple commands: \"Throttle ALL: all throttle axes\", \"add the collectives to Throttle\", " +
                           "\"remove War Thunder from Throttle\", \"rename Throttle to Throttle ALL\". Free sentences need the local AI model.";
        }

        private static void OfflineAdd(Toolbox box, EngineRelationAssistantResult result, string relation, string what, bool create)
        {
            OfflineAdd(box, result, relation, new List<string> { what }, create);
        }

        // Every part ("all thrust axes", "collectives", "spaceship forward/backward thrusters") is resolved on its own;
        // the kind comes from the open relation, the words ("axis", "buttons") or what most results are.
        private static void OfflineAdd(Toolbox box, EngineRelationAssistantResult result, string relation, List<string> whats, bool create)
        {
            string existing = box.FindRelation(relation);
            string kind = existing != null ? (InternalDataManagement.AllRelations[existing].ISAXIS ? "axis" : "button") : whats.Select(KindOf).FirstOrDefault(k => k.Length > 0) ?? string.Empty;
            List<string> sets = new List<string>();
            List<ActionRef> found = new List<ActionRef>();
            List<string> missed = new List<string>();
            foreach (string what in whats)
            {
                string query = CleanQuery(what);
                if (query.Length == 0)
                    continue;
                EngineRelationAssistantStep resolve = new EngineRelationAssistantStep { Step = result.Steps.Count + 1 };
                result.Steps.Add(resolve);
                ToolCall call = new ToolCall { Tool = "resolve", Query = query, Kind = kind };
                resolve.Tool = call.Tool;
                resolve.Call = call.Describe();
                box.Execute(call, resolve);
                if (box.LastSet.Count == 0)
                {
                    missed.Add(query);
                    continue;
                }
                sets.Add(box.LastSetRef);
                found.AddRange(box.LastSet);
            }
            if (found.Count == 0)
            {
                result.NeedsModel = missed.Count == 0;
                result.Reply = missed.Count == 0
                    ? "I did not understand what to add. Name the function, e.g. \"all throttle axes\"."
                    : "I found nothing for \"" + string.Join("\", \"", missed) + "\". Try other words, or use the search under Add actions.";
                return;
            }
            bool isAxis = existing != null ? InternalDataManagement.AllRelations[existing].ISAXIS : (kind.Length > 0 ? kind == "axis" : found.Count(a => a.IsAxis) * 2 >= found.Count);
            if (existing == null)
            {
                if (!create && relation.Length == 0)
                {
                    result.Reply = "Open a relation first, or name one: \"Throttle ALL: all throttle axes\".";
                    return;
                }
                Offline(box, result, new ToolCall { Tool = "create_relation", Name = relation, Kind = isAxis ? "axis" : "button" });
                existing = box.FindRelation(relation);
                if (existing == null)
                    return;
            }
            Offline(box, result, new ToolCall { Tool = "add_to_relation", Relation = existing, Ids = sets });
            result.Finished = true;
            result.Reply = box.SummaryLine(existing) + (missed.Count > 0 ? " Nothing found for \"" + string.Join("\", \"", missed) + "\"." : string.Empty);
        }

        // Offline remove: the relation's actions whose game, aircraft or title has the words.
        private static void OfflineRemove(Toolbox box, EngineRelationAssistantResult result, string relation, string what)
        {
            string existing = box.FindRelation(relation);
            if (existing == null)
            {
                result.Reply = relation.Length == 0 ? "Open a relation first, or say which one: \"remove War Thunder from Throttle\"." : "There is no relation \"" + relation + "\".";
                return;
            }
            EngineRelationAssistantStep show = new EngineRelationAssistantStep { Step = result.Steps.Count + 1, Tool = "show_relation", Call = "show_relation(\"" + existing + "\")" };
            result.Steps.Add(show);
            box.Execute(new ToolCall { Tool = "show_relation", Relation = existing }, show);
            List<string> words = EngineRelationSearch.Terms(Regex.Replace(what, @"\b(the|all|every|actions?|entries|of)\b", " ", RegexOptions.IgnoreCase));
            if (words.Count == 0)
            {
                result.Reply = "Say what to remove, e.g. \"remove War Thunder\" or \"remove the helicopters\" (that one needs the AI model).";
                return;
            }
            List<string> ids = new List<string>();
            foreach (ActionRef action in box.LastSet)
            {
                string hay = (action.Game + " " + EngineRelationAssistantGameNames.Label(action.Game) + " " + action.Title + " " + string.Join(" ", action.Planes)).ToLowerInvariant();
                string compact = Regex.Replace(hay, "[^a-z0-9]", string.Empty);
                if (words.All(w => hay.Contains(w) || compact.Contains(Regex.Replace(w, "[^a-z0-9]", string.Empty))))
                    ids.Add(action.Ref);
            }
            if (ids.Count == 0)
            {
                result.NeedsModel = true;
                result.Reply = "Nothing in \"" + existing + "\" matches \"" + what + "\". Classes like \"helicopters\" or \"WW2 aircraft\" need the local AI model.";
                return;
            }
            Offline(box, result, new ToolCall { Tool = "remove_from_relation", Relation = existing, Ids = ids });
            result.Finished = true;
            result.Reply = box.SummaryLine(existing);
        }

        private static void Offline(Toolbox box, EngineRelationAssistantResult result, ToolCall call)
        {
            EngineRelationAssistantStep step = new EngineRelationAssistantStep { Step = result.Steps.Count + 1, Tool = call.Tool, Call = call.Describe() };
            result.Steps.Add(step);
            string answer = box.Execute(call, step);
            if (!step.Ok && result.Reply.Length == 0)
                result.Reply = answer.StartsWith("ERROR ", StringComparison.Ordinal) ? answer.Substring(6) : answer;
        }

        private static string KindOf(string what)
        {
            string t = " " + what.ToLowerInvariant() + " ";
            if (Regex.IsMatch(t, @"\b(axis|axes|analog|analogue|slider|lever)\b"))
                return "axis";
            if (Regex.IsMatch(t, @"\b(button|buttons|switch|switches|key|keys|toggle|press)\b"))
                return "button";
            return string.Empty;
        }

        // "all throttle axes" -> "throttle": the function words only.
        private static string CleanQuery(string what)
        {
            string text = Regex.Replace(what, @"\b(all|every|each|the|of|my|for|games?|aircraft|planes?|actions?|inputs?|binds?|bind|bound|axis|axes|analog|analogue|buttons?|relation|one|single|that|which|to|in|into|on|same|controls?|functions?|ones?|any|also)\b", " ", RegexOptions.IgnoreCase);
            // Plural to singular for the last word ("collectives" finds "Collective").
            text = Regex.Replace(Regex.Replace(text, @"\s+", " ").Trim(' ', ',', '.', ';'), @"(?<=\w{4})s$", string.Empty);
            return text;
        }

        // ------------------------------------------------------------------------------------------------------
        // The model's answer: one JSON tool call
        // ------------------------------------------------------------------------------------------------------

        internal static ToolCall ParseCall(string content, out string error)
        {
            error = string.Empty;
            Dictionary<string, object> json;
            try
            {
                json = EngineSerialization.FromJson<Dictionary<string, object>>(EngineNlGroup.ExtractJsonObject(content ?? string.Empty));
            }
            catch (Exception ex)
            {
                error = "Not JSON (" + ex.Message + ").";
                return null;
            }
            if (json == null)
            {
                error = "No JSON object.";
                return null;
            }
            // Accept {"tool":..,"args":{..}} too: the arguments may come nested.
            Dictionary<string, object> args = EngineNlGroup.Get(json, "args") as Dictionary<string, object> ?? EngineNlGroup.Get(json, "arguments") as Dictionary<string, object> ?? json;
            ToolCall call = new ToolCall { Tool = (EngineNlGroup.Text(json, "tool") ?? string.Empty).Trim().ToLowerInvariant() };
            if (!Tools.Contains(call.Tool))
            {
                error = "Unknown tool '" + call.Tool + "'. Tools: " + string.Join(", ", Tools) + ".";
                return null;
            }
            call.Query = (EngineNlGroup.Text(args, "query") ?? string.Empty).Trim();
            call.Kind = (EngineNlGroup.Text(args, "kind") ?? string.Empty).Trim().ToLowerInvariant();
            call.Games = EngineNlGroup.Strings(EngineNlGroup.Get(args, "games")).Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
            call.Relation = (EngineNlGroup.Text(args, "relation") ?? string.Empty).Trim();
            call.Name = (EngineNlGroup.Text(args, "name") ?? string.Empty).Trim();
            call.NewName = (EngineNlGroup.Text(args, "new_name") ?? string.Empty).Trim();
            call.Ids = EngineNlGroup.Strings(EngineNlGroup.Get(args, "ids")).Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            call.Reply = (EngineNlGroup.Text(args, "reply") ?? string.Empty).Trim();
            if (call.Kind == "axes")
                call.Kind = "axis";
            if (call.Kind == "buttons")
                call.Kind = "button";
            return call;
        }

        // GBNF: one flat JSON object per tool. Ids are A<n> or S<n> (JoyPro checks each against this turn's results).
        internal static string BuildGrammar()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("root ::= ws ( listrel | showrel | resolve | search | suggest | create | rename | add | remove | done ) ws");
            sb.AppendLine("listrel ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"list_relations\\\"\" ws \"}\"");
            sb.AppendLine("showrel ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"show_relation\\\"\" ws \",\" ws \"\\\"relation\\\":\" ws str ws \"}\"");
            sb.AppendLine("resolve ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"resolve\\\"\" ws \",\" ws \"\\\"query\\\":\" ws str ws \",\" ws \"\\\"kind\\\":\" ws kind ws \"}\"");
            sb.AppendLine("search ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"search\\\"\" ws \",\" ws \"\\\"query\\\":\" ws str ws \",\" ws \"\\\"kind\\\":\" ws kind ( ws \",\" ws \"\\\"games\\\":\" ws strlist )? ws \"}\"");
            sb.AppendLine("suggest ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"suggest\\\"\" ws \",\" ws \"\\\"relation\\\":\" ws str ws \"}\"");
            sb.AppendLine("create ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"create_relation\\\"\" ws \",\" ws \"\\\"name\\\":\" ws str ws \",\" ws \"\\\"kind\\\":\" ws relkind ws \"}\"");
            sb.AppendLine("rename ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"rename_relation\\\"\" ws \",\" ws \"\\\"relation\\\":\" ws str ws \",\" ws \"\\\"new_name\\\":\" ws str ws \"}\"");
            sb.AppendLine("add ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"add_to_relation\\\"\" ws \",\" ws \"\\\"relation\\\":\" ws str ws \",\" ws \"\\\"ids\\\":\" ws ids ws \"}\"");
            sb.AppendLine("remove ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"remove_from_relation\\\"\" ws \",\" ws \"\\\"relation\\\":\" ws str ws \",\" ws \"\\\"ids\\\":\" ws ids ws \"}\"");
            sb.AppendLine("done ::= \"{\" ws \"\\\"tool\\\":\" ws \"\\\"done\\\"\" ws \",\" ws \"\\\"reply\\\":\" ws reply ws \"}\"");
            sb.AppendLine("kind ::= \"\\\"axis\\\"\" | \"\\\"button\\\"\" | \"\\\"any\\\"\"");
            sb.AppendLine("relkind ::= \"\\\"axis\\\"\" | \"\\\"button\\\"\"");
            sb.AppendLine("ids ::= \"[\" ws id ( ws \",\" ws id ){0,199} ws \"]\"");
            sb.AppendLine("id ::= \"\\\"\" [AS] [1-9] [0-9]{0,3} \"\\\"\"");
            sb.AppendLine("strlist ::= \"[\" ws str ( ws \",\" ws str ){0,9} ws \"]\"");
            sb.AppendLine("str ::= \"\\\"\" [^\"\\\\\\n]{1,80} \"\\\"\"");
            sb.AppendLine("reply ::= \"\\\"\" [^\"\\\\\\n]{1,400} \"\\\"\"");
            sb.AppendLine("ws ::= [ \\t\\n]{0,3}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------
        // The tools, run on the profile
        // ------------------------------------------------------------------------------------------------------

        internal sealed class Toolbox
        {
            private readonly EngineRelationAssistantResult result;
            private readonly Dictionary<string, ActionRef> refs = new Dictionary<string, ActionRef>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ActionRef> byKey = new Dictionary<string, ActionRef>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<ActionRef>> sets = new Dictionary<string, List<ActionRef>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, EngineRelationAssistantRelation> touched = new Dictionary<string, EngineRelationAssistantRelation>(StringComparer.Ordinal);
            private List<EngineRelationSuggest.Command> index;
            private Dictionary<string, EngineRelationSuggest.Command> commandByKey;

            public List<ActionRef> LastSet = new List<ActionRef>();
            public int MaxLines = 45;
            private readonly List<string> queries = new List<string>();

            // A part of the request counts as looked up when a resolve or search of this turn shared a word with it.
            public bool Covered(string part)
            {
                List<string> words = EngineRelationSearch.Terms(part).Where(w => w.Length >= 4).Select(Stem).Distinct().ToList();
                return words.Count == 0 || queries.Any(q =>
                {
                    HashSet<string> asked = new HashSet<string>(EngineRelationSearch.Terms(q).Select(Stem));
                    return words.Count(asked.Contains) * 2 >= words.Count;
                });
            }

            private static string Stem(string word)
            {
                string w = word.ToLowerInvariant();
                return w.Length > 5 ? w.Substring(0, 5) : w;
            }
            public string LastSetRef = string.Empty;
            public string LastActivity = string.Empty;

            public Toolbox(EngineRelationAssistantResult result)
            {
                this.result = result;
            }

            private List<EngineRelationSuggest.Command> Index()
            {
                if (index == null)
                {
                    index = EngineRelationSuggest.Index(result.Warnings);
                    commandByKey = new Dictionary<string, EngineRelationSuggest.Command>(StringComparer.OrdinalIgnoreCase);
                    foreach (EngineRelationSuggest.Command command in index)
                    {
                        string key = command.Game + "|" + command.Id + "|" + command.Plane;
                        if (!commandByKey.ContainsKey(key))
                            commandByKey[key] = command;
                    }
                }
                return index;
            }

            public string FindRelation(string name)
            {
                string wanted = (name ?? string.Empty).Trim().Trim('"', '\'');
                if (wanted.Length == 0)
                    return null;
                if (InternalDataManagement.AllRelations.ContainsKey(wanted))
                    return wanted;
                return InternalDataManagement.AllRelations.Keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
            }

            // "axis, 12 actions in DCS, IL-2"
            public string Brief(string name)
            {
                Relation relation = InternalDataManagement.AllRelations[name];
                List<RelationItem> nodes = (relation.NODES ?? new List<RelationItem>()).Where(n => n != null).ToList();
                List<string> games = nodes.Select(n => EngineRelationAssistantGameNames.Label(EngineRelationSuggest.GameKey(n.Game ?? string.Empty))).Distinct().ToList();
                return (relation.ISAXIS ? "axis" : "button") + ", " + nodes.Count.ToString(CultureInfo.InvariantCulture) + " actions" + (games.Count > 0 ? " in " + string.Join(", ", games.Take(8)) + (games.Count > 8 ? " ..." : string.Empty) : string.Empty);
            }

            public string Execute(ToolCall call, EngineRelationAssistantStep step)
            {
                string answer;
                try
                {
                    switch (call.Tool)
                    {
                        case "list_relations": answer = ListRelations(); break;
                        case "show_relation": answer = ShowRelation(call); break;
                        case "resolve": answer = Find(call, true); break;
                        case "search": answer = Find(call, false); break;
                        case "suggest": answer = Suggest(call); break;
                        case "create_relation": answer = Create(call); break;
                        case "rename_relation": answer = Rename(call); break;
                        case "add_to_relation": answer = Change(call, true, step); break;
                        case "remove_from_relation": answer = Change(call, false, step); break;
                        default: answer = "ERROR Unknown tool."; break;
                    }
                }
                catch (Exception ex)
                {
                    answer = "ERROR " + ex.Message;
                }
                step.Ok = !answer.StartsWith("ERROR", StringComparison.Ordinal);
                if (!step.Ok)
                    step.Error = answer.Substring(Math.Min(6, answer.Length));
                int newline = answer.IndexOf('\n');
                step.Result = newline > 0 ? answer.Substring(0, newline) : answer;
                return answer;
            }

            private string ListRelations()
            {
                LastActivity = "reading your relations";
                List<string> names = InternalDataManagement.AllRelations.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                StringBuilder sb = new StringBuilder("RESULT " + names.Count.ToString(CultureInfo.InvariantCulture) + " relations");
                foreach (string name in names.Take(80))
                    sb.Append('\n').Append(name).Append(" | ").Append(Brief(name));
                if (names.Count > 80)
                    sb.Append("\n... and ").Append((names.Count - 80).ToString(CultureInfo.InvariantCulture)).Append(" more");
                return sb.ToString();
            }

            private string ShowRelation(ToolCall call)
            {
                string name = FindRelation(call.Relation);
                if (name == null)
                    return NoRelation(call.Relation);
                LastActivity = "reading '" + name + "'";
                Index();
                Relation relation = InternalDataManagement.AllRelations[name];
                List<ActionRef> actions = new List<ActionRef>();
                foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                    List<string> planes = game == "DCS"
                        ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key).ToList()
                        : ((node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(a => a)) ? new List<string> { game } : new List<string>());
                    if (planes.Count == 0)
                        continue;
                    EngineRelationSuggest.Command command;
                    commandByKey.TryGetValue(game + "|" + node.ID + "|" + planes[0], out command);
                    ActionRef action = Register(game, node.ID, command != null ? command.Title : node.ID, relation.ISAXIS, planes, true);
                    actions.Add(action);
                }
                string set = NewSet(actions);
                return "RESULT " + set + " \"" + name + "\" (" + (relation.ISAXIS ? "axis" : "button") + ") has " + actions.Count.ToString(CultureInfo.InvariantCulture) + " actions" +
                       Lines(actions, Math.Max(60, MaxLines));
            }

            // resolve: JoyPro's knowledge (functions, synonyms); search: the words in the titles.
            private string Find(ToolCall call, bool brain)
            {
                if (call.Query.Length == 0)
                    return "ERROR Give a query.";
                bool? axis = call.Kind == "axis" ? true : call.Kind == "button" ? (bool?)false : null;
                LastActivity = (brain ? "looking up '" : "searching '") + call.Query + "'";
                queries.Add(call.Query);
                List<EngineRelationSearchHit> hits;
                string understood = string.Empty;
                if (brain)
                {
                    hits = EngineRelationAssistantBrain.Resolve(call.Query, axis, call.Games, result.Warnings, out understood);
                }
                else
                {
                    EngineRelationSearchResult search = EngineRelationSearch.Search(call.Query, call.Games.Count > 0 ? call.Games : null, null, axis, 120);
                    hits = search.Results;
                    if (search.Functions.Count > 0)
                        understood = string.Join(", ", search.Functions);
                }
                List<ActionRef> actions = new List<ActionRef>();
                foreach (EngineRelationSearchHit hit in hits)
                {
                    ActionRef action = Register(hit.Game, hit.Id, hit.Title, hit.IsAxis, hit.Planes, true);
                    foreach (string other in hit.InRelations)
                        action.InRelations.Add(other);
                    action.SameIdDifferent = Math.Max(action.SameIdDifferent, hit.SameIdDifferent);
                    if (hit.MeaningNote.Length > 0)
                        action.MeaningNote = hit.MeaningNote;
                    if (!actions.Contains(action))
                        actions.Add(action);
                }
                string set = NewSet(actions);
                if (actions.Count == 0)
                    return "RESULT " + set + " " + call.Tool + " \"" + call.Query + "\": nothing found" + (understood.Length > 0 ? " (understood as " + understood + ")" : string.Empty) + ". Try other words.";
                return "RESULT " + set + " " + call.Tool + " \"" + call.Query + "\": " + actions.Count.ToString(CultureInfo.InvariantCulture) + " actions" +
                       (understood.Length > 0 ? " (understood as " + understood + ")" : string.Empty) + " in " + GameCounts(actions) +
                       ". " + set + " stands for all " + actions.Count.ToString(CultureInfo.InvariantCulture) + "." + Lines(actions, MaxLines);
            }

            private string Suggest(ToolCall call)
            {
                string name = FindRelation(call.Relation);
                if (name == null)
                    return NoRelation(call.Relation);
                LastActivity = "asking what fits '" + name + "'";
                EngineRelationSuggestResult suggest = EngineRelationSuggest.Suggest(name, "medium", 60);
                List<ActionRef> actions = new List<ActionRef>();
                StringBuilder lines = new StringBuilder();
                foreach (EngineRelationSuggestion suggestion in suggest.Suggestions)
                {
                    ActionRef action = Register(suggestion.Game, suggestion.Id, suggestion.Title, suggestion.IsAxis, suggestion.Planes, true);
                    foreach (string other in suggestion.InOtherRelations)
                        action.InRelations.Add(other);
                    action.SameIdDifferent = Math.Max(action.SameIdDifferent, suggestion.SameIdDifferent);
                    if (suggestion.MeaningNote.Length > 0)
                        action.MeaningNote = suggestion.MeaningNote;
                    actions.Add(action);
                    if (actions.Count <= 40)
                        lines.Append('\n').Append(Line(action)).Append(" | ").Append(suggestion.Confidence).Append(": ").Append(suggestion.Why);
                }
                string set = NewSet(actions);
                string function = string.Join(", ", suggest.Functions.Where(f => f.Weight >= 0.5).Select(f => f.Name));
                return "RESULT " + set + " suggest \"" + name + "\"" + (function.Length > 0 ? " (function: " + function + ")" : string.Empty) + ": " +
                       actions.Count.ToString(CultureInfo.InvariantCulture) + " suggestions" + lines + (actions.Count > 40 ? "\n... " + (actions.Count - 40).ToString(CultureInfo.InvariantCulture) + " more in " + set : string.Empty);
            }

            private string Create(ToolCall call)
            {
                string name = Regex.Replace(call.Name ?? string.Empty, @"\s+", " ").Trim().Trim('"', '\'');
                if (name.Length == 0)
                    return "ERROR Give the new relation a name.";
                if (name.Length > 80)
                    return "ERROR The name is too long.";
                string existing = FindRelation(name);
                if (existing != null)
                    return "ERROR \"" + existing + "\" exists already (" + Brief(existing) + "). Add to it, or pick another name.";
                if (call.Kind != "axis" && call.Kind != "button")
                    return "ERROR Say kind axis or button.";
                LastActivity = "creating '" + name + "'";
                Relation relation = new Relation();
                relation.NAME = name;
                relation.ISAXIS = call.Kind == "axis";
                relation.Groups = new List<string>();
                relation.NODES = new List<RelationItem>();
                InternalDataManagement.AllRelations[name] = relation;
                Touch(name).Created = true;
                result.Changes.Add(new EngineRelationAssistantChange { Relation = name, Kind = "created", Title = relation.ISAXIS ? "axis" : "button" });
                return "RESULT created \"" + name + "\" (" + call.Kind + ", empty). Now add actions.";
            }

            private string Rename(ToolCall call)
            {
                string name = FindRelation(call.Relation);
                if (name == null)
                    return NoRelation(call.Relation);
                string wanted = Regex.Replace(call.NewName ?? string.Empty, @"\s+", " ").Trim().Trim('"', '\'');
                if (wanted.Length == 0 || wanted.Length > 80)
                    return "ERROR Give the new name.";
                string clash = FindRelation(wanted);
                if (clash != null && clash != name)
                    return "ERROR \"" + clash + "\" exists already.";
                string final = Program.RenameRelation(name, wanted);
                if (string.IsNullOrEmpty(final))
                    return "ERROR Could not rename \"" + name + "\".";
                LastActivity = "renaming '" + name + "'";
                EngineRelationAssistantRelation entry;
                if (touched.TryGetValue(name, out entry))
                {
                    touched.Remove(name);
                    entry.Name = final;
                    touched[final] = entry;
                }
                else
                {
                    entry = Touch(final);
                }
                if (entry.RenamedFrom.Length == 0 && !entry.Created)
                    entry.RenamedFrom = name;
                foreach (EngineRelationAssistantChange change in result.Changes.Where(c => c.Relation == name))
                    change.Relation = final;
                if (result.FocusRelation == name)
                    result.FocusRelation = final;
                result.Changes.Add(new EngineRelationAssistantChange { Relation = final, Kind = "renamed", Title = name });
                return "RESULT renamed \"" + name + "\" to \"" + final + "\".";
            }

            // add_to_relation / remove_from_relation: every id is checked against this turn's results.
            private string Change(ToolCall call, bool add, EngineRelationAssistantStep step)
            {
                string name = FindRelation(call.Relation);
                if (name == null)
                    return NoRelation(call.Relation);
                if (call.Ids.Count == 0)
                    return "ERROR Give ids.";
                Relation relation = InternalDataManagement.AllRelations[name];
                List<ActionRef> actions = new List<ActionRef>();
                List<string> unknown = new List<string>();
                foreach (string id in call.Ids)
                {
                    List<ActionRef> set;
                    ActionRef action;
                    if (sets.TryGetValue(id, out set))
                        actions.AddRange(set);
                    else if (refs.TryGetValue(id, out action))
                        actions.Add(action);
                    else
                        unknown.Add(id);
                }
                foreach (string id in unknown)
                {
                    if (!result.Rejected.Contains(id))
                        result.Rejected.Add(id);
                    step.Rejected.Add(id);
                }
                actions = actions.Distinct().ToList();
                List<string> wrongKind = new List<string>();
                List<string> specs = new List<string>();
                List<ActionRef> applied = new List<ActionRef>();
                Dictionary<string, List<string>> current = Active(relation);
                foreach (ActionRef action in actions)
                {
                    if (add && action.KindKnown && action.IsAxis != relation.ISAXIS)
                    {
                        wrongKind.Add(action.Ref);
                        continue;
                    }
                    List<string> have;
                    current.TryGetValue(action.Game + "|" + action.Id, out have);
                    have = have ?? new List<string>();
                    List<string> planes = add
                        ? action.Planes.Where(p => !have.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList()
                        : action.Planes.Where(p => have.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (planes.Count == 0)
                        continue;
                    specs.Add(action.Spec(planes));
                    applied.Add(action);
                    EngineRelationAssistantRelation entry = Touch(name);
                    Dictionary<string, int> counts = add ? entry.Added : entry.Removed;
                    string label = EngineRelationAssistantGameNames.Label(action.Game);
                    counts[label] = (counts.ContainsKey(label) ? counts[label] : 0) + (action.Game == "DCS" ? planes.Count : 1);
                    result.Changes.Add(new EngineRelationAssistantChange
                    {
                        Relation = name, Kind = add ? "added" : "removed", Game = action.Game, Id = action.Id, Title = action.Title, Planes = planes,
                        OtherRelations = action.InRelations.Where(r => r != name).ToList()
                    });
                    if (add)
                    {
                        foreach (string other in action.InRelations.Where(r => r != name && !entry.AlsoIn.Contains(r, StringComparer.OrdinalIgnoreCase)))
                            entry.AlsoIn.Add(other);
                    }
                }
                if (wrongKind.Count > 0)
                    step.Skipped.AddRange(wrongKind);
                int changed = 0;
                if (specs.Count > 0)
                {
                    LastActivity = (add ? "adding to '" : "removing from '") + name + "'";
                    EngineRelationSuggestApplyResult done = add
                        ? EngineRelationSuggest.Apply(name, specs, new List<string>(), new List<string>(), new List<string>())
                        : EngineRelationSuggest.Apply(name, new List<string>(), new List<string>(), specs, new List<string>());
                    changed = add ? done.Added : done.Removed;
                    foreach (string error in done.Errors)
                        result.Warnings.Add(error);
                }
                StringBuilder sb = new StringBuilder();
                sb.Append("RESULT ").Append(add ? "added " : "removed ").Append(applied.Count.ToString(CultureInfo.InvariantCulture)).Append(" actions")
                  .Append(add ? " to \"" : " from \"").Append(name).Append("\"");
                if (applied.Count > 0)
                    sb.Append(" (").Append(GameCounts(applied)).Append(")");
                sb.Append(". It now has ").Append(Active(relation).Count.ToString(CultureInfo.InvariantCulture)).Append(" actions.");
                if (unknown.Count > 0)
                    sb.Append("\nERROR unknown ids rejected: ").Append(string.Join(", ", unknown)).Append(". Use only ids from RESULT lines.");
                if (wrongKind.Count > 0)
                    sb.Append("\nSkipped (").Append(relation.ISAXIS ? "buttons in an axis relation" : "axes in a button relation").Append("): ").Append(string.Join(", ", wrongKind.Take(20)));
                if (actions.Count > 0 && applied.Count == 0 && wrongKind.Count == 0)
                    sb.Append(add ? "\nAll of them were in the relation already." : "\nNone of them was in the relation.");
                if (unknown.Count > 0 && applied.Count == 0)
                    return "ERROR unknown ids: " + string.Join(", ", unknown) + ". Use only ids from RESULT lines.";
                return sb.ToString();
            }

            // game|id -> the active aircraft (DCS) or the game itself.
            private static Dictionary<string, List<string>> Active(Relation relation)
            {
                Dictionary<string, List<string>> active = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                    List<string> planes = game == "DCS"
                        ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key).ToList()
                        : ((node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(a => a)) ? new List<string> { game } : new List<string>());
                    if (planes.Count == 0)
                        continue;
                    string key = game + "|" + node.ID;
                    List<string> list;
                    if (!active.TryGetValue(key, out list))
                        active[key] = list = new List<string>();
                    list.AddRange(planes);
                }
                return active;
            }

            private ActionRef Register(string game, string id, string title, bool isAxis, IEnumerable<string> planes, bool kindKnown)
            {
                string gameKey = EngineRelationSuggest.GameKey(game ?? string.Empty);
                string key = gameKey + "|" + id + "|" + (title ?? string.Empty);
                ActionRef action;
                if (!byKey.TryGetValue(key, out action))
                {
                    action = new ActionRef
                    {
                        Ref = "A" + (refs.Count + 1).ToString(CultureInfo.InvariantCulture), Game = gameKey, Id = id ?? string.Empty, Title = title ?? string.Empty, IsAxis = isAxis, KindKnown = kindKnown
                    };
                    refs[action.Ref] = action;
                    byKey[key] = action;
                }
                foreach (string plane in planes ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(plane))
                        action.Planes.Add(gameKey == "DCS" ? plane : gameKey);
                }
                return action;
            }

            private string NewSet(List<ActionRef> actions)
            {
                string id = "S" + (sets.Count + 1).ToString(CultureInfo.InvariantCulture);
                sets[id] = actions.ToList();
                LastSet = actions;
                LastSetRef = id;
                return id;
            }

            private static string Lines(List<ActionRef> actions, int max)
            {
                StringBuilder sb = new StringBuilder();
                foreach (ActionRef action in actions.Take(max))
                    sb.Append('\n').Append(Line(action));
                if (actions.Count > max)
                    sb.Append("\n... and ").Append((actions.Count - max).ToString(CultureInfo.InvariantCulture)).Append(" more (titles: ")
                      .Append(string.Join("; ", actions.Skip(max).Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(25))).Append(")");
                return sb.ToString();
            }

            // "A12 DCS F-16C_50, F-15C +3 | Throttle | axis | in 'Throttle'"
            private static string Line(ActionRef action)
            {
                List<string> planes = action.Planes.ToList();
                string where = action.Game == "DCS"
                    ? "DCS " + string.Join(", ", planes.Take(3)) + (planes.Count > 3 ? " +" + (planes.Count - 3).ToString(CultureInfo.InvariantCulture) : string.Empty)
                    : EngineRelationAssistantGameNames.Label(action.Game);
                return action.Ref + " " + where + " | " + action.Title + " | " + (action.IsAxis ? "axis" : "button") +
                       (action.InRelations.Count > 0 ? " | in '" + string.Join("', '", action.InRelations.Take(2)) + "'" : string.Empty) +
                       (action.SameIdDifferent > 0 ? " | same id means something else in " + action.SameIdDifferent.ToString(CultureInfo.InvariantCulture) + " other aircraft (not in this line)" : string.Empty);
            }

            private static string GameCounts(IEnumerable<ActionRef> actions)
            {
                return string.Join(", ", actions.GroupBy(a => EngineRelationAssistantGameNames.Label(a.Game))
                    .OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture)));
            }

            private string NoRelation(string name)
            {
                List<string> close = InternalDataManagement.AllRelations.Keys
                    .Where(k => (name ?? string.Empty).Length > 0 && k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(5).ToList();
                return "ERROR There is no relation \"" + name + "\"." + (close.Count > 0 ? " Did you mean \"" + string.Join("\", \"", close) + "\"?" : " Use list_relations, or create_relation.");
            }

            private EngineRelationAssistantRelation Touch(string name)
            {
                EngineRelationAssistantRelation entry;
                if (!touched.TryGetValue(name, out entry))
                {
                    entry = new EngineRelationAssistantRelation { Name = name };
                    touched[name] = entry;
                }
                return entry;
            }

            // "Throttle ALL: added DCS 48 aircraft, IL-2 1, Star Citizen 2"
            public string SummaryLine(string name)
            {
                EngineRelationAssistantRelation entry;
                string final = touched.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
                if (!touched.TryGetValue(final, out entry))
                    return "Nothing changed in \"" + name + "\".";
                return entry.Describe();
            }

            // The turn's summary; relations created this turn that stayed empty are taken out again.
            public void Finish()
            {
                foreach (EngineRelationAssistantRelation entry in touched.Values.ToList())
                {
                    Relation relation;
                    if (entry.Created && InternalDataManagement.AllRelations.TryGetValue(entry.Name, out relation) &&
                        (relation.NODES == null || !relation.NODES.Any(n => n != null && n.AIRCRAFT != null && n.AIRCRAFT.Values.Any(a => a))))
                    {
                        InternalDataManagement.AllRelations.Remove(entry.Name);
                        result.Warnings.Add("\"" + entry.Name + "\" stayed empty and was not kept.");
                        result.Changes.RemoveAll(c => c.Relation == entry.Name);
                        touched.Remove(entry.Name);
                        continue;
                    }
                    if (InternalDataManagement.AllRelations.TryGetValue(entry.Name, out relation))
                        entry.Actions = Active(relation).Count;
                }
                result.Relations.AddRange(touched.Values.Where(t => t.Created || t.RenamedFrom.Length > 0 || t.Added.Count > 0 || t.Removed.Count > 0));
                if (string.IsNullOrEmpty(result.FocusRelation) || !InternalDataManagement.AllRelations.ContainsKey(result.FocusRelation))
                {
                    EngineRelationAssistantRelation first = result.Relations.FirstOrDefault(r => r.Created) ?? result.Relations.FirstOrDefault();
                    result.FocusRelation = first != null ? first.Name : (InternalDataManagement.AllRelations.ContainsKey(result.FocusRelation ?? string.Empty) ? result.FocusRelation : string.Empty);
                }
                result.Summary = result.Relations.Count == 0 ? "Nothing changed." : string.Join("\n", result.Relations.Select(r => r.Describe()));
                result.Changed = result.Relations.Count > 0;
            }
        }

        public static string ToText(EngineRelationAssistantResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Request: " + result.Message);
            sb.AppendLine("Method: " + result.Method + (result.Llm.Used ? " (" + result.Llm.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms, model process stopped: " + (result.Llm.ProcessExited ? "yes" : "no") + ")" : string.Empty));
            foreach (EngineRelationAssistantStep step in result.Steps)
            {
                sb.AppendLine("  " + step.Step.ToString(CultureInfo.InvariantCulture) + ". " + (step.Call.Length > 0 ? step.Call : "(no tool call)") +
                              (step.Result.Length > 0 ? " -> " + step.Result : string.Empty) + (step.Error.Length > 0 && step.Result.Length == 0 ? " -> error: " + step.Error : string.Empty) +
                              (step.Rejected.Count > 0 ? " [rejected: " + string.Join(", ", step.Rejected) + "]" : string.Empty));
            }
            sb.AppendLine("Summary:");
            foreach (string line in result.Summary.Split('\n'))
                sb.AppendLine("  " + line);
            if (result.Reply.Length > 0)
                sb.AppendLine("Reply: " + result.Reply);
            if (result.Rejected.Count > 0)
                sb.AppendLine("Rejected ids: " + string.Join(", ", result.Rejected));
            if (result.NeedsModel)
                sb.AppendLine("Needs the local AI model.");
            if (result.Llm.Prompt.Length > 0)
                sb.AppendLine(result.Llm.Prompt);
            foreach (string warning in result.Warnings.Distinct().Where(w => !w.Contains("action catalog not found")).Take(12))
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    // The offline brain's resolve for the assistant: what the user names, in every game, with synonyms; a function the
    // catalog knows comes first, title matches only when they are strong; side actions sharing a word are left out.
    internal static class EngineRelationAssistantBrain
    {
        // Words people use for the same function in different games.
        private static readonly Dictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "throttle", new[] { "thrust", "power lever", "engines throttle" } },
            { "thrust", new[] { "throttle" } },
            { "collective", new[] { "helicopter collective" } },
            { "forward thrust", new[] { "strafe forward", "throttle forward", "forward backward", "strafe longitudinal" } },
            { "strafe", new[] { "strafe forward", "strafe lateral", "strafe vertical" } },
            { "flare", new[] { "countermeasure", "flares" } },
            { "chaff", new[] { "countermeasure" } },
            { "gear", new[] { "landing gear", "undercarriage" } },
            { "airbrake", new[] { "speed brake", "air brake" } },
            { "speed brake", new[] { "airbrake", "air brake" } },
            { "pitch", new[] { "elevator" } },
            { "roll", new[] { "aileron", "bank" } },
            { "yaw", new[] { "rudder" } },
            { "rudder", new[] { "yaw" } },
            { "forward/backward", new[] { "strafe longitudinal", "forward thrust axis" } },
            { "forward backward", new[] { "strafe longitudinal", "forward thrust axis" } },
            { "fore/aft", new[] { "strafe longitudinal", "forward thrust axis" } },
            { "longitudinal", new[] { "strafe longitudinal", "forward thrust axis" } },
            { "thruster", new[] { "strafe longitudinal", "forward thrust axis" } }
        };

        private static readonly string[] SideWords =
        {
            "friction", "trim", "detent", "cutoff", "cut-off", "cut off", "mixture", "propeller", "prop ", "zoom", "antenna", "cursor", "slew", "radar", "brake",
            "volume", "brightness", "light", "fov", "camera", "view", "idle", "limiter", "elevation", "starter", "fuel", "cowl", "radiator", "boost", "supercharger",
            "tdc", "governor", "reverser", "reverse", "wheel", "designator", " lock", "lateral", "vertical", "horizontal", "vector", "mining", "weapon",
            "altitude", "tractor", "salvage", "turret", "scan", " eva ", "invert", "landing)", "alternate"
        };

        public static List<EngineRelationSearchHit> Resolve(string query, bool? axis, List<string> games, List<string> warnings, out string understood)
        {
            // The function map (engine relations resolve): every game's action of the functions the words name, with
            // their synonyms and game terms. Functions it only lists as related come in when nothing else does.
            List<EngineRelationSearchHit> mapped = ResolveByMap(query, axis, games, warnings, out understood);
            if (mapped.Count > 0)
                return mapped;
            return ResolveBySearch(query, axis, games, warnings, out understood);
        }

        private static List<EngineRelationSearchHit> ResolveByMap(string query, bool? axis, List<string> games, List<string> warnings, out string understood)
        {
            understood = string.Empty;
            List<EngineRelationSearchHit> hits = new List<EngineRelationSearchHit>();
            EngineResolveResult resolved;
            try
            {
                resolved = EngineRelationFunctions.Resolve(query, games != null && games.Count > 0 ? games : null, null, axis, 0);
            }
            catch (Exception ex)
            {
                warnings.Add("The function map could not resolve \"" + query + "\" (" + ex.Message + ").");
                return hits;
            }
            warnings.AddRange(resolved.Warnings);
            HashSet<string> direct = new HashSet<string>(resolved.Functions.Where(f => !f.Related).Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            bool onlyRelated = direct.Count == 0;
            string lower = " " + (query ?? string.Empty).ToLowerInvariant() + " ";
            foreach (EngineResolvedAction action in resolved.Games.SelectMany(g => g.Actions))
            {
                if (!onlyRelated && !direct.Contains(action.Function))
                    continue;
                string title = " " + action.Title.ToLowerInvariant().Replace('_', ' ') + " ";
                if (SideWords.Any(w => title.Contains(w) && !lower.Contains(w.Trim())))
                    continue;
                hits.Add(new EngineRelationSearchHit
                {
                    Game = action.Game, Id = action.Id, Title = action.Title, Category = action.Category, IsAxis = action.IsAxis,
                    Planes = action.Planes.ToList(), Score = (int)Math.Round(action.Weight * 100), Why = action.Why,
                    Variants = action.Variants, Near = action.Near, SameIdDifferent = action.SameIdDifferent, MeaningNote = action.MeaningNote
                });
            }
            List<string> names = resolved.Functions.Where(f => onlyRelated || !f.Related).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            understood = (resolved.DidYouMean.Length > 0 ? "did you mean " + resolved.DidYouMean + "; " : string.Empty) + string.Join(", ", names);
            return hits;
        }

        // Without the function map (or when it knows none of the words): the word search with a few synonyms.
        private static List<EngineRelationSearchHit> ResolveBySearch(string query, bool? axis, List<string> games, List<string> warnings, out string understood)
        {
            understood = string.Empty;
            string text = (query ?? string.Empty).Trim();
            List<string> queries = new List<string> { text };
            string lower = text.ToLowerInvariant();
            foreach (KeyValuePair<string, string[]> synonym in Synonyms)
            {
                if (Regex.IsMatch(lower, @"\b" + Regex.Escape(synonym.Key) + @"\b"))
                    queries.AddRange(synonym.Value);
            }
            Dictionary<string, EngineRelationSearchHit> hits = new Dictionary<string, EngineRelationSearchHit>(StringComparer.OrdinalIgnoreCase);
            List<string> functions = new List<string>();
            foreach (string q in queries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                EngineRelationSearchResult search = EngineRelationSearch.Search(q, games != null && games.Count > 0 ? games : null, null, axis, 400);
                functions.AddRange(search.Functions);
                bool original = string.Equals(q, text, StringComparison.OrdinalIgnoreCase);
                foreach (EngineRelationSearchHit hit in search.Results)
                {
                    // A catalog function of the user's own words counts; a title match only when strong (the synonyms
                    // only by title and nearly exact: "collective pitch" must not bring the whole pitch function).
                    bool function = original && hit.Why.StartsWith("function", StringComparison.OrdinalIgnoreCase);
                    if (!function && hit.Score < (original ? 85 : 92))
                        continue;
                    string title = " " + hit.Title.ToLowerInvariant().Replace('_', ' ') + " ";
                    if (SideWords.Any(w => title.Contains(w) && !lower.Contains(w.Trim())))
                        continue;
                    string key = hit.Game + "|" + hit.Id + "|" + hit.Title;
                    EngineRelationSearchHit known;
                    if (!hits.TryGetValue(key, out known) || known.Score < hit.Score)
                        hits[key] = hit;
                }
            }
            understood = string.Join(", ", functions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)) +
                         (queries.Count > 1 ? (functions.Count > 0 ? "; " : string.Empty) + "with the synonyms " + string.Join(", ", queries.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase)) : string.Empty);
            return hits.Values.OrderByDescending(h => h.Score).ThenBy(h => h.Game, StringComparer.Ordinal).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    internal static class EngineRelationAssistantGameNames
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IL2Game", "IL-2" }, { "IL2Korea", "IL-2 Korea" }, { "IL21946", "IL-2 1946" }, { "IL2CoD", "IL-2 Cliffs of Dover" }, { "IL2CoDBlitz", "IL-2 CoD Blitz" },
            { "StarCitizen", "Star Citizen" }, { "WarThunder", "War Thunder" }, { "FalconBMS", "Falcon BMS" }, { "EliteDangerous", "Elite Dangerous" },
            { "MSFS2020", "MSFS 2020" }, { "MSFS2024", "MSFS 2024" }, { "XPlane11", "X-Plane 11" }, { "XPlane12", "X-Plane 12" }, { "NuclearOption", "Nuclear Option" },
            { "VTOLVR", "VTOL VR" }, { "VTOLVRNative", "VTOL VR" }, { "ArmaReforger", "Arma Reforger" }, { "TinyCombatArena", "Tiny Combat Arena" }, { "GRebels", "G-Rebels" }
        };

        public static string Label(string game)
        {
            string name;
            return Names.TryGetValue(game ?? string.Empty, out name) ? name : game ?? string.Empty;
        }
    }

    public sealed class EngineRelationAssistantStep
    {
        public int Step { get; set; }
        public string Tool { get; set; }
        public string Call { get; set; }
        public string Result { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<string> Rejected { get; set; }
        public List<string> Skipped { get; set; }
        public string Raw { get; set; }
        public long DurationMs { get; set; }

        public EngineRelationAssistantStep()
        {
            Tool = Call = Result = Error = Raw = string.Empty;
            Rejected = new List<string>();
            Skipped = new List<string>();
        }
    }

    public sealed class EngineRelationAssistantChange
    {
        public string Relation { get; set; }
        public string Kind { get; set; }
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public List<string> Planes { get; set; }
        public List<string> OtherRelations { get; set; }

        public EngineRelationAssistantChange()
        {
            Relation = Kind = Game = Id = Title = string.Empty;
            Planes = new List<string>();
            OtherRelations = new List<string>();
        }
    }

    // One relation the turn created or changed: what was added and removed per game (DCS: aircraft, other games: actions).
    public sealed class EngineRelationAssistantRelation
    {
        public string Name { get; set; }
        public bool Created { get; set; }
        public string RenamedFrom { get; set; }
        public Dictionary<string, int> Added { get; set; }
        public Dictionary<string, int> Removed { get; set; }
        public int Actions { get; set; }
        public List<string> AlsoIn { get; set; }

        public EngineRelationAssistantRelation()
        {
            Name = RenamedFrom = string.Empty;
            Added = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AlsoIn = new List<string>();
        }

        public string Describe()
        {
            List<string> parts = new List<string>();
            if (Created)
                parts.Add("created");
            if (RenamedFrom.Length > 0)
                parts.Add("renamed from \"" + RenamedFrom + "\"");
            if (Added.Count > 0)
                parts.Add("added " + Counts(Added));
            if (Removed.Count > 0)
                parts.Add("removed " + Counts(Removed));
            string text = "\"" + Name + "\": " + (parts.Count == 0 ? "no change" : string.Join("; ", parts));
            if (AlsoIn.Count > 0)
                text += " (some are also in \"" + string.Join("\", \"", AlsoIn.Take(3)) + "\")";
            return text;
        }

        private static string Counts(Dictionary<string, int> counts)
        {
            return string.Join(", ", counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Key + " " + c.Value.ToString(CultureInfo.InvariantCulture) + (c.Key == "DCS" ? (c.Value == 1 ? " aircraft" : " aircraft") : string.Empty)));
        }
    }

    public sealed class EngineRelationAssistantResult
    {
        public string Message { get; set; }
        public string FocusRelation { get; set; }
        public string Method { get; set; }
        public bool Finished { get; set; }
        public bool Changed { get; set; }
        public bool NeedsModel { get; set; }
        public string Reply { get; set; }
        public string Summary { get; set; }
        public List<EngineRelationAssistantRelation> Relations { get; set; }
        public List<EngineRelationAssistantChange> Changes { get; set; }
        public List<EngineRelationAssistantStep> Steps { get; set; }
        public List<string> Rejected { get; set; }
        public EngineNlLlmInfo Llm { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationAssistantResult()
        {
            Message = FocusRelation = Method = Reply = Summary = string.Empty;
            Relations = new List<EngineRelationAssistantRelation>();
            Changes = new List<EngineRelationAssistantChange>();
            Steps = new List<EngineRelationAssistantStep>();
            Rejected = new List<string>();
            Llm = new EngineNlLlmInfo();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal static partial class Program
    {
        // relations assistant "<message>" [--relation <name>] [--llm auto|on|off] [--max-steps N] [--show-prompt]
        private static int AssistantRelations(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            EngineRelationAssistant.Options options = new EngineRelationAssistant.Options();
            options.LlmMode = (PopOptionValue(args, "--llm") ?? string.Empty).Trim().ToLowerInvariant();
            options.ShowPrompt = PopFlag(args, "--show-prompt");
            options.Relation = PopOptionValue(args, "--relation") ?? string.Empty;
            string steps = PopOptionValue(args, "--max-steps");
            if (steps != null && (!int.TryParse(steps, NumberStyles.Integer, CultureInfo.InvariantCulture, out options.MaxSteps) || options.MaxSteps < 1))
                return Fail("--max-steps takes a number from 1.");
            if (options.LlmMode.Length > 0 && !EngineNlGroup.LlmModes.Contains(options.LlmMode))
                return Fail("Invalid --llm value: " + options.LlmMode + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
            List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown relations assistant argument(s): " + string.Join(" ", unknown));
            if (args.Count == 0)
                return Fail("relations assistant \"<message>\" [--relation <name>] [--llm auto|on|off] [--max-steps N] [--show-prompt]");
            options.Message = string.Join(" ", args);
            EngineRelationAssistantResult result = EngineRelationAssistant.Run(options);
            if (result.Errors.Count > 0 && !result.Changed)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            if (result.Changed)
            {
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }
            EmitPayload(textOutput ? EngineRelationAssistant.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // relations rename <relation> <new name>: the bind stays with it.
        private static int RenameRelationCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count != 2 || args[1].Trim().Length == 0)
                return Fail("relations rename <relation> <new name>");
            List<string> candidates;
            string relationName;
            if (!TryResolveRelationName(args[0], out relationName, out candidates))
                return FailResolution("relation", args[0], candidates);
            string wanted = args[1].Trim();
            string clash = InternalDataManagement.AllRelations.Keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
            if (clash != null && clash != relationName)
                return Fail("A relation named '" + clash + "' exists already.");
            string final = RenameRelation(relationName, wanted);
            if (string.IsNullOrEmpty(final))
                return Fail("Could not rename '" + relationName + "'.");
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            EmitPayload(textOutput ? "Renamed: " + relationName + " -> " + final : EngineSerialization.ToJson(new { Relation = relationName, Name = final }), outPath);
            return 0;
        }

        // Renames a relation and its bind; a case-only change keeps the name unique.
        internal static string RenameRelation(string oldName, string newName)
        {
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return oldName;
            return RenameRelationEntry(oldName, newName);
        }
    }
}
