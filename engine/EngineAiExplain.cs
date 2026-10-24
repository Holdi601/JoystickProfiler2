using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Questions to the JoyPro assistant and to Manage / Edit with AI ("what does Landing gear down do in X-Plane 12",
    // "what is boost in Elite", "explain the FSD jump", "which action is the gear in MSFS?"): answered from JoyPro's own
    // knowledge of every game, with or without the local AI model, and nothing changes. The answer names the action
    // by its readable title, the game by its name, says axis or button, where the game files it, what it does (the
    // cockpit guide, the sim's own description or the function's general explanation; never invented) and which of the
    // user's relations already has it. No ids.
    internal static class EngineAiExplain
    {
        private static readonly Regex QuestionStart = new Regex(
            @"^(?:please\s+|hey\s+|so\s+)?(?:what(?:'s|\s+is|\s+are|\s+does|\s+do|\s+would)|wat\s+does|explain|tell\s+me\s+(?:about|what)|describe\s+what|which\s+(?:action|command|button|key|axis|control|input|one)s?\b|how\s+does|where\s+is|is\s+there\s+(?:an?\s+)?(?:action|command|button|axis))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CommandStart = new Regex(@"^(?:please\s+)?(?:add|put|make|create|new|remove|delete|drop|rename|append|prepend|expand|extend|fill|check|move|tick|untick|group|bind)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsQuestion(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0 || CommandStart.IsMatch(t))
                return false;
            return QuestionStart.IsMatch(t) || (t.EndsWith("?", StringComparison.Ordinal) && Regex.IsMatch(t, @"^(?:what|which|how|where|is|does|do|can)\b", RegexOptions.IgnoreCase));
        }

        // The answer to a question, or empty when the text is no question.
        public static string Answer(string text)
        {
            if (!IsQuestion(text))
                return string.Empty;
            string rest;
            List<string> games = EngineGameWords.Extract(text.Trim().TrimEnd('?', '.', '!'), out rest);
            string phrase = Regex.Replace(" " + rest + " ",
                @"\b(?:please|hey|so|what's|what|wat|is|are|does|do|would|explain|tell|me|about|describe|which|how|where|there|an?|the|action|command|button|key|control|input|one|mean|means|in|on|for|of|game|games|thing|exactly|it|this|that)\b",
                " ", RegexOptions.IgnoreCase);
            phrase = Regex.Replace(phrase, @"[""'?!,;:]+", " ");
            phrase = Regex.Replace(phrase, @"\s+", " ").Trim();
            string where = games.Count == 0 ? "any game" : string.Join(", ", games.Select(EngineGameWords.Label).Distinct());
            if (phrase.Length == 0)
                return "Name the action you want to know about, e.g. \"what does Landing gear down do in X-Plane 12\".";
            EngineRelationSearchResult search = EngineRelationSearch.Search(phrase, games.Count > 0 ? games : null, null, null, 40);
            List<EngineRelationSearchHit> hits = search.Results.Where(h => games.Count == 0 || games.Contains(h.Game, StringComparer.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                return "I found no action \"" + phrase + "\" in " + where + ". Try other words, or look it up under Add actions.";
            EngineRelationSearchHit best = hits.FirstOrDefault(h => string.Equals(Readable(h), phrase, StringComparison.OrdinalIgnoreCase)) ?? hits[0];
            return Describe(best, hits.Where(h => h != best).ToList());
        }

        // What the local model is told about the games (every AI path): the games with a command list on this PC (name =
        // the id to pass), what the request names and, per named game, its actions for the request's function words, by
        // readable title. Ships are ships, not aircraft.
        public static string ModelGameContext(string request)
        {
            StringBuilder sb = new StringBuilder();
            List<string> known = EngineGameWords.KnownGames();
            sb.Append("GAMES with a command list (name = id for \"games\"): ")
              .Append(string.Join(", ", known.Select(g => EngineGameWords.Label(g) == g ? g : EngineGameWords.Label(g) + " = " + g)))
              .Append(". Star Citizen, Elite Dangerous and X4 fly ships (boost, strafe, landing gear, jump drive); the others aircraft, helicopters or vehicles.");
            string rest = request ?? string.Empty;
            List<string> named = EngineGameWords.EveryGame(request) ? new List<string>() : EngineGameWords.Extract(request ?? string.Empty, out rest);
            if (EngineGameWords.EveryGame(request))
            {
                sb.Append("\nThe request means every game.");
                return sb.ToString();
            }
            if (named.Count == 0)
                return sb.ToString();
            sb.Append("\nThe request names ").Append(string.Join(", ", named.Select(EngineGameWords.Label).Distinct())).Append(": use \"games\":[")
              .Append(string.Join(",", named.Select(g => "\"" + g + "\""))).Append("] so that only their actions come back.");
            string words = Regex.Replace(" " + rest + " ", @"\b(?:add|put|make|create|new|a|an|the|of|in|into|to|for|with|from|relation|relations|called|named|expand|extend|fill|also|all|every|my|please|what|does|do|is|mean)\b", " ", RegexOptions.IgnoreCase);
            words = Regex.Replace(words, @"[""'?!,;:]+", " ");
            words = Regex.Replace(words, @"\s+", " ").Trim();
            if (words.Length == 0)
                return sb.ToString();
            foreach (string game in named.Take(4))
            {
                List<EngineRelationSearchHit> hits;
                try
                {
                    hits = EngineRelationAssistantBrain.Resolve(words, null, new List<string> { game }, new List<string>(), out rest);
                }
                catch (Exception)
                {
                    continue;
                }
                List<string> titles = hits.Where(h => string.Equals(h.Game, game, StringComparison.OrdinalIgnoreCase))
                    .Select(h => Readable(h) + " (" + (h.IsAxis ? "axis" : "button") + ")").Distinct().Take(8).ToList();
                sb.Append("\n").Append(EngineGameWords.Label(game)).Append(" actions for \"").Append(words).Append("\": ")
                  .Append(titles.Count > 0 ? string.Join("; ", titles) : "none found (the game may not have it)").Append(".");
            }
            return sb.ToString();
        }

        // The function the action is, by name ("Afterburner / boost"): the search's label, else the map's.
        private static string FunctionName(EngineRelationSearchHit hit)
        {
            if (!string.IsNullOrEmpty(hit.FunctionLabel))
                return hit.FunctionLabel;
            string id = !string.IsNullOrEmpty(hit.Function) ? hit.Function : (hit.Functions ?? new List<string>()).Select(f => f.Split(':')[0]).FirstOrDefault() ?? string.Empty;
            if (id.Length == 0)
                return string.Empty;
            try
            {
                EngineFunctionMap.Function f;
                return EngineFunctionMap.Load(new List<string>()).ById.TryGetValue(id, out f) ? f.Name : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Readable(EngineRelationSearchHit hit)
        {
            return EngineActionInfo.Readable(hit.Game, hit.Id, hit.Title);
        }

        // "flight_controls" -> "flight controls", "space/Steering: Analog" -> "space > Steering: Analog".
        internal static string ReadableCategory(string category)
        {
            string c = (category ?? string.Empty).Trim();
            if (c.Length == 0)
                return c;
            c = c.Replace("___", " > ").Replace("/", " > ").Replace('_', ' ');
            return Regex.Replace(c, @"\s+", " ").Trim();
        }

        private static string Describe(EngineRelationSearchHit hit, List<EngineRelationSearchHit> others)
        {
            string game = EngineGameWords.Label(hit.Game);
            string title = Readable(hit);
            string plane = hit.Game == "DCS" && hit.Planes != null && hit.Planes.Count > 0 ? hit.Planes[0] : hit.Game;
            EngineActionInfo.Info info = EngineActionInfo.Describe(hit.Game, plane, hit.Id, hit.Title, hit.Function, hit.FunctionLabel);
            string category = ReadableCategory(hit.Category);
            StringBuilder sb = new StringBuilder();
            sb.Append("In ").Append(game).Append(", \"").Append(title).Append("\" is ").Append(hit.IsAxis ? "an axis" : "a button");
            if (category.Length > 0)
                sb.Append(" (").Append(category).Append(")");
            if (hit.Game == "DCS" && hit.Planes != null && hit.Planes.Count > 0)
                sb.Append(" of ").Append(hit.Planes.Count == 1 ? hit.Planes[0] : hit.Planes.Count.ToString(CultureInfo.InvariantCulture) + " aircraft (" + string.Join(", ", hit.Planes.Take(3)) + (hit.Planes.Count > 3 ? " ..." : string.Empty) + ")");
            sb.Append(".");
            string does = EngineActionInfo.InGameWords(hit.Game, hit.Category, Regex.Replace(info.Explanation ?? string.Empty, @"\s+", " ").Trim());
            if (does.Length > 0)
            {
                if (does.Length > 320)
                    does = does.Substring(0, 317) + "...";
                sb.Append(" What it does: ").Append(does).Append(does.EndsWith(".", StringComparison.Ordinal) ? string.Empty : ".");
                sb.Append(info.Source == "guide" ? " (cockpit guide)" : info.Source == "game" ? " (the game's own description)" : " (general explanation of the function)");
            }
            else if (!string.IsNullOrEmpty(FunctionName(hit)))
            {
                sb.Append(" JoyPro knows it as the function \"").Append(FunctionName(hit)).Append("\"; the game has no description of it.");
            }
            else
            {
                sb.Append(" The game has no description of it.");
            }
            List<string> relations = (hit.InRelations ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
            sb.Append(relations.Count > 0
                ? " It is in your relation" + (relations.Count > 1 ? "s " : " ") + string.Join(", ", relations.Take(4).Select(r => "\"" + r + "\"")) + "."
                : " It is in none of your relations yet.");
            List<string> close = others.Where(o => o.Game == hit.Game).Select(Readable).Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (close.Count > 0)
                sb.Append(" Close matches: ").Append(string.Join(", ", close.Select(c => "\"" + c + "\""))).Append(".");
            return sb.ToString();
        }
    }
}
