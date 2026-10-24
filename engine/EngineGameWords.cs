using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Game names in free text, for every AI path (the JoyPro assistant, Manage / Edit with AI, fill, the chat): the
    // words of DB\Functions\language.json "games" (full names, short names: "msfs", "xp12", "wt", "sc", "elite", "x4",
    // a word for several games: "MSFS", "X-Plane", "Arma", "ACE COMBAT"), then every game integration's id, name and
    // aliases. The longest phrase wins; "x-plane 12", "xplane12" and "xp 12" are the same. A game is named by its
    // readable name (Label) everywhere a user reads it, never by its id ("XPlane12").
    internal static class EngineGameWords
    {
        internal sealed class Mention
        {
            public string Term = string.Empty;
            public string Label = string.Empty;
            public List<string> Games = new List<string>();
            public int Start;
            public int Length;
        }

        private sealed class Entry
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public List<string> Games = new List<string>();
        }

        private static readonly object Sync = new object();
        private static Dictionary<string, Entry> byTerm;
        private static Dictionary<string, string> labels;
        private static int longest;

        private static readonly Dictionary<string, string> BuiltInLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", "DCS" }, { "IL2Game", "IL-2" }, { "StarCitizen", "Star Citizen" }
        };

        // Tests and a changed language.json.
        internal static void Reset()
        {
            lock (Sync)
            {
                byTerm = null;
                labels = null;
            }
        }

        private static void Load()
        {
            lock (Sync)
            {
                if (byTerm != null)
                    return;
                Dictionary<string, Entry> terms = new Dictionary<string, Entry>(StringComparer.Ordinal);
                Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> pair in BuiltInLabels)
                    names[pair.Key] = pair.Value;
                foreach (IGameIntegration game in GameRegistry.All)
                    names[game.GameId] = Regex.Replace(game.DisplayName ?? game.GameId, @"\s*\([^)]*\)", string.Empty).Trim();
                try
                {
                    string file = Path.Combine(EngineNlGroup.FunctionsFolder(), "language.json");
                    if (File.Exists(file))
                    {
                        Dictionary<string, object> json = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                        foreach (Dictionary<string, object> item in EngineNlGroup.Objects(EngineNlGroup.Get(json, "games")))
                        {
                            string id = EngineNlGroup.Text(item, "id");
                            if (id.Length == 0)
                                continue;
                            string label = EngineNlGroup.Text(item, "label");
                            List<string> members = EngineNlGroup.Strings(EngineNlGroup.Get(item, "members")).Where(m => m.Length > 0).ToList();
                            Entry entry = new Entry { Id = id, Label = label.Length > 0 ? label : id, Games = members.Count > 0 ? members : new List<string> { id } };
                            if (members.Count == 0 && label.Length > 0)
                                names[id] = label;
                            foreach (string term in EngineNlGroup.Strings(EngineNlGroup.Get(item, "terms")).Concat(new[] { label, id }))
                                AddTerm(terms, term, entry, false);
                        }
                    }
                }
                catch (Exception)
                {
                }
                foreach (KeyValuePair<string, string> pair in BuiltInLabels)
                {
                    Entry entry = new Entry { Id = pair.Key, Label = names[pair.Key], Games = new List<string> { pair.Key } };
                    AddTerm(terms, pair.Key, entry, false);
                    AddTerm(terms, pair.Value, entry, false);
                }
                foreach (IGameIntegration game in GameRegistry.All)
                {
                    Entry entry = new Entry { Id = game.GameId, Label = names[game.GameId], Games = new List<string> { game.GameId } };
                    AddTerm(terms, game.GameId, entry, false);
                    AddTerm(terms, names[game.GameId], entry, false);
                    AddTerm(terms, game.DisplayName, entry, false);
                    // Short command-line aliases ("ar", "ed", "a3") are no words of a sentence.
                    foreach (string alias in game.Aliases ?? new List<string>())
                        AddTerm(terms, alias, entry, true);
                }
                longest = terms.Keys.Count == 0 ? 1 : terms.Keys.Max(k => k.Split(' ').Length);
                labels = names;
                byTerm = terms;
            }
        }

        // Aliases that mean something else in a sentence: the Korean War era, a DCS wingman command, VTOL aircraft.
        private static readonly HashSet<string> NoSentenceWords = new HashSet<string>(StringComparer.Ordinal) { "korea", "nuclear", "wingman", "vtol", "blitz", "cod", "byoj", "1946" };

        private static void AddTerm(Dictionary<string, Entry> terms, string term, Entry entry, bool alias)
        {
            string key = Normalize(term);
            if (key.Length == 0 || (alias && (key.Replace(" ", string.Empty).Length <= 2 || NoSentenceWords.Contains(key))))
                return;
            if (!terms.ContainsKey(key))
                terms[key] = entry;
            string compact = key.Replace(" ", string.Empty);
            if (compact != key && compact.Length > 2 && !terms.ContainsKey(compact))
                terms[compact] = entry;
        }

        // Lower case, words only: "X-Plane 12" -> "x plane 12", "IL-2" -> "il 2", "Elite: Dangerous" -> "elite dangerous".
        internal static string Normalize(string text)
        {
            StringBuilder sb = new StringBuilder();
            bool space = false;
            foreach (char c in (text ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (space && sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(c);
                    space = false;
                }
                else
                {
                    space = true;
                }
            }
            return sb.ToString();
        }

        // The readable name of a game id ("XPlane12" -> "X-Plane 12", "IL2Game" -> "IL-2").
        public static string Label(string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                return string.Empty;
            Load();
            string key = EngineAutoGroup.IsDcs(game) ? "DCS" : game.Trim();
            string label;
            return labels.TryGetValue(key, out label) && label.Length > 0 ? label : key;
        }

        // Every game name in the text, longest first, without overlaps; positions are in the original text.
        public static List<Mention> Find(string text)
        {
            Load();
            List<Mention> mentions = new List<Mention>();
            List<Match> tokens = Regex.Matches(text ?? string.Empty, @"[A-Za-z0-9]+").Cast<Match>().ToList();
            int i = 0;
            while (i < tokens.Count)
            {
                Mention found = null;
                for (int n = Math.Min(longest + 1, tokens.Count - i); n >= 1 && found == null; n--)
                {
                    List<string> words = tokens.Skip(i).Take(n).Select(t => t.Value.ToLowerInvariant()).ToList();
                    // Words joined by a space or written together ("xplane 12", "ace combat7").
                    foreach (string key in new[] { string.Join(" ", words), string.Join(string.Empty, words) })
                    {
                        Entry entry;
                        if (byTerm.TryGetValue(key, out entry))
                        {
                            int start = tokens[i].Index;
                            int end = tokens[i + n - 1].Index + tokens[i + n - 1].Length;
                            found = new Mention { Term = text.Substring(start, end - start), Label = entry.Label, Games = entry.Games.ToList(), Start = start, Length = end - start };
                            i += n;
                            break;
                        }
                    }
                }
                if (found != null)
                    mentions.Add(found);
                else
                    i++;
            }
            return mentions;
        }

        // The games the text names (a word for several games stands for all of them) and the text without them.
        public static List<string> Extract(string text, out string rest)
        {
            List<Mention> mentions = Find(text);
            List<string> games = mentions.SelectMany(m => m.Games).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            StringBuilder sb = new StringBuilder(text ?? string.Empty);
            foreach (Mention mention in mentions.OrderByDescending(m => m.Start))
                sb.Remove(mention.Start, mention.Length).Insert(mention.Start, " ");
            rest = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return games;
        }

        public static List<string> Games(string text)
        {
            string rest;
            return Extract(text, out rest);
        }

        // A name for one game or a few ("X-Plane 12", "msfs", "Star Citizen"): the game ids, or none.
        public static List<string> Resolve(string name)
        {
            string rest;
            List<string> games = Extract(name, out rest);
            rest = Regex.Replace(rest, @"\b(?:all|every|each|the|of|my|in|from|for|game|games|aircraft|modules?|and|or|,)\b", " ", RegexOptions.IgnoreCase).Trim(' ', ',', '.', ';');
            return rest.Length == 0 ? games : new List<string>();
        }

        // "every game", "all games", "each game", "all my games"
        public static bool EveryGame(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"\b(?:every|each|all(?:\s+(?:my|the|of\s+the))?)\s+(?:games?|sims?|simulators?)\b", RegexOptions.IgnoreCase);
        }

        // The games with a command list on this PC (what the AI can be told about), readable names, sorted.
        public static List<string> KnownGames()
        {
            return EngineRelationSuggest.Index(new List<string>()).Select(c => c.Game).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g == "DCS" ? 0 : 1).ThenBy(Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // What the games are: aircraft, ships, vehicles - the word a sentence about them uses.
        public static string Things(IEnumerable<string> games)
        {
            List<string> list = (games ?? new string[0]).ToList();
            if (list.Count > 0 && list.All(g => SpaceGames.Contains(g)))
                return "ships";
            if (list.Any(g => SpaceGames.Contains(g)))
                return "aircraft and ships";
            return "aircraft";
        }

        internal static readonly HashSet<string> SpaceGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "StarCitizen", "EliteDangerous", "X4" };
    }
}
