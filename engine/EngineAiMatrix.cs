using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // engine relations ai-matrix [--games g1|g2] [--paths p1|p2] [--examples N]: every AI path of the Relations page
    // (JoyPro assistant, Manage / Edit with AI, Expand with AI = fill, By description, the relation chat and what the
    // local model is shown) asked typical requests for every game JoyPro knows, on the loaded profile in memory (each
    // cell is taken back at once, nothing is saved). Each cell is judged: actions of the named game only, a readable
    // answer (no raw ids), an answer at all; a game without that function must not get another game's actions.
    // The oracle of "the game has this function" is the function map (relations resolve, filtered to the game).
    internal static class EngineAiMatrix
    {
        internal sealed class GameCase
        {
            public string Game = string.Empty;
            public string Name = string.Empty;
            public string Alias = string.Empty;
            public bool Space;
        }

        // What a user types for each game (the integration names carry "(experimental)" and the like).
        private static readonly Dictionary<string, string[]> Spoken = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", new[] { "DCS", "dcs world" } },
            { "IL2Game", new[] { "IL-2 Great Battles", "il2" } },
            { "IL2Korea", new[] { "IL-2 Korea", "il2 korea" } },
            { "IL21946", new[] { "IL-2 1946", "il2 1946" } },
            { "IL2CoD", new[] { "IL-2 Cliffs of Dover", "cliffs of dover" } },
            { "IL2CoDBlitz", new[] { "Cliffs of Dover Blitz", "cod blitz" } },
            { "StarCitizen", new[] { "Star Citizen", "sc" } },
            { "EliteDangerous", new[] { "Elite Dangerous", "elite" } },
            { "X4", new[] { "X4 Foundations", "x4" } },
            { "MSFS2020", new[] { "Microsoft Flight Simulator 2020", "msfs 2020" } },
            { "MSFS2024", new[] { "MSFS 2024", "fs2024" } },
            { "XPlane11", new[] { "X-Plane 11", "xp11" } },
            { "XPlane12", new[] { "X-Plane 12", "xp12" } },
            { "WarThunder", new[] { "War Thunder", "wt" } },
            { "FalconBMS", new[] { "Falcon BMS", "bms" } },
            { "VTOLVR", new[] { "VTOL VR", "vtolvr" } },
            { "VTOLVRNative", new[] { "VTOL VR native", "vtol vr native" } },
            { "NuclearOption", new[] { "Nuclear Option", "nuclear option" } },
            { "ProjectWingman", new[] { "Project Wingman", "project wingman" } },
            { "AceCombat7", new[] { "ACE COMBAT 7", "ac7" } },
            { "AceCombat8", new[] { "ACE COMBAT 8", "ac8" } },
            { "Battlefield6", new[] { "Battlefield 6", "bf6" } },
            { "Battlefield2042", new[] { "Battlefield 2042", "bf2042" } },
            { "TinyCombatArena", new[] { "Tiny Combat Arena", "tca" } },
            { "GRebels", new[] { "G-Rebels", "grebels" } },
            { "HLLVietnam", new[] { "Hell Let Loose Vietnam", "hll vietnam" } },
            { "Wardogs", new[] { "WARDOGS", "wardogs" } },
            { "Arma3", new[] { "Arma 3", "arma3" } },
            { "Arma2", new[] { "Arma 2", "arma2" } },
            { "Arma2OA", new[] { "Arma 2 Operation Arrowhead", "arma 2 oa" } },
            { "ArmaReforger", new[] { "Arma Reforger", "reforger" } },
            { "FSX", new[] { "FSX", "fsx" } },
            { "FSXSE", new[] { "FSX Steam Edition", "fsx se" } },
            { "P3D4", new[] { "Prepar3D v4", "p3d4" } },
            { "P3D5", new[] { "Prepar3D v5", "p3d5" } },
            { "P3D6", new[] { "Prepar3D v6", "p3d6" } }
        };

        private static readonly HashSet<string> SpaceGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "StarCitizen", "EliteDangerous", "X4" };

        // Function words per game kind: [0] a button function to create, [1] landing gear, [2] an axis, [3] a light
        // or a ship's jump drive.
        private static string[] Words(GameCase game)
        {
            if (!game.Space)
                return new[] { "flaps", "landing gear", "pitch", "landing lights" };
            string jump = game.Game == "EliteDangerous" ? "FSD jump" : game.Game == "StarCitizen" ? "quantum jump" : "travel drive";
            return new[] { "boost", "landing gear", "strafe up", jump };
        }

        public static List<GameCase> Games(IEnumerable<string> only)
        {
            HashSet<string> wanted = new HashSet<string>(only ?? new string[0], StringComparer.OrdinalIgnoreCase);
            List<string> ids = new List<string> { "DCS", "IL2Game", "StarCitizen" };
            ids.AddRange(GameRegistry.All.Select(g => g.GameId));
            List<GameCase> list = new List<GameCase>();
            foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (wanted.Count > 0 && !wanted.Contains(id))
                    continue;
                string[] spoken;
                IGameIntegration integration;
                if (!Spoken.TryGetValue(id, out spoken))
                    spoken = new[] { GameRegistry.TryResolve(id, out integration) ? Regex.Replace(integration.DisplayName, @"\s*\([^)]*\)", string.Empty) : id, id };
                list.Add(new GameCase { Game = id, Name = spoken[0], Alias = spoken[1], Space = SpaceGames.Contains(id) });
            }
            return list;
        }

        // ------------------------------------------------------------------------------------------------------
        // The profile in memory: kept before a cell, taken back after it
        // ------------------------------------------------------------------------------------------------------

        private sealed class Snapshot
        {
            private readonly Dictionary<string, Relation> relations = new Dictionary<string, Relation>(StringComparer.Ordinal);
            private readonly Dictionary<string, Bind> binds = new Dictionary<string, Bind>(StringComparer.Ordinal);
            private readonly Dictionary<Relation, Tuple<string, bool, List<RelationItem>, List<string>, string, Bind>> state = new Dictionary<Relation, Tuple<string, bool, List<RelationItem>, List<string>, string, Bind>>();
            private readonly Dictionary<RelationItem, Dictionary<string, bool>> ticks = new Dictionary<RelationItem, Dictionary<string, bool>>();

            public Snapshot()
            {
                foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
                {
                    relations[pair.Key] = pair.Value;
                    if (pair.Value == null || state.ContainsKey(pair.Value))
                        continue;
                    Relation r = pair.Value;
                    state[r] = Tuple.Create(r.NAME, r.ISAXIS, r.NODES == null ? null : r.NODES.ToList(), r.Groups == null ? null : r.Groups.ToList(), r.Function, r.bind);
                    foreach (RelationItem node in r.NODES ?? new List<RelationItem>())
                        if (node != null && !ticks.ContainsKey(node))
                            ticks[node] = node.AIRCRAFT == null ? null : new Dictionary<string, bool>(node.AIRCRAFT);
                }
                foreach (KeyValuePair<string, Bind> pair in InternalDataManagement.AllBinds)
                    binds[pair.Key] = pair.Value;
            }

            public void Restore()
            {
                InternalDataManagement.AllRelations.Clear();
                foreach (KeyValuePair<string, Relation> pair in relations)
                    InternalDataManagement.AllRelations[pair.Key] = pair.Value;
                InternalDataManagement.AllBinds.Clear();
                foreach (KeyValuePair<string, Bind> pair in binds)
                    InternalDataManagement.AllBinds[pair.Key] = pair.Value;
                foreach (KeyValuePair<Relation, Tuple<string, bool, List<RelationItem>, List<string>, string, Bind>> pair in state)
                {
                    pair.Key.NAME = pair.Value.Item1;
                    pair.Key.ISAXIS = pair.Value.Item2;
                    pair.Key.NODES = pair.Value.Item3;
                    pair.Key.Groups = pair.Value.Item4;
                    pair.Key.Function = pair.Value.Item5;
                    pair.Key.bind = pair.Value.Item6;
                }
                foreach (KeyValuePair<RelationItem, Dictionary<string, bool>> pair in ticks)
                    pair.Key.AIRCRAFT = pair.Value;
            }

            // game|id|plane of every switched-on node, per relation (the cell's diff).
            public static HashSet<string> Active()
            {
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
                {
                    foreach (RelationItem node in (pair.Value == null ? null : pair.Value.NODES) ?? new List<RelationItem>())
                    {
                        if (node == null || string.IsNullOrWhiteSpace(node.ID))
                            continue;
                        string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                        // Per relation: an action moved from another relation counts as added to its new one.
                        if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0)
                        {
                            set.Add(game + "\t" + node.ID + "\t" + game + "\t" + pair.Key);
                            continue;
                        }
                        foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.Where(p => p.Value))
                            set.Add(game + "\t" + node.ID + "\t" + (game == "DCS" ? plane.Key : game) + "\t" + pair.Key);
                    }
                }
                return set;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // One cell
        // ------------------------------------------------------------------------------------------------------

        private sealed class Found
        {
            public readonly List<EngineAiMatrixAction> Actions = new List<EngineAiMatrixAction>();
            public readonly StringBuilder Text = new StringBuilder();
            public bool NeedsModel;
            public string Error = string.Empty;
            public bool Question;

            public void Add(string game, string id, string title)
            {
                string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
                if (!Actions.Any(a => a.Game == g && a.Id == id))
                    Actions.Add(new EngineAiMatrixAction { Game = g, Id = id ?? string.Empty, Title = title ?? string.Empty });
            }

            public void Say(string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Text.AppendLine(text.Trim());
            }
        }

        public static EngineAiMatrixResult Run(List<string> games, List<string> paths, int examples)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EngineAiMatrixResult result = new EngineAiMatrixResult();
            HashSet<string> wanted = new HashSet<string>(paths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Func<string, bool> on = p => wanted.Count == 0 || wanted.Contains(p) || wanted.Contains(p.Split(':')[0]);
            List<string> warnings = new List<string>();
            EngineRelationSuggest.Index(warnings);
            Dictionary<string, int> perGame = EngineRelationSuggest.Index(warnings).GroupBy(c => c.Game).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (GameCase game in Games(games))
            {
                int actions;
                perGame.TryGetValue(game.Game, out actions);
                string[] w = Words(game);
                string focus = "Gear TOGGLE";
                string expand = "Roll";
                // The game's own action for the question: the best one of the landing gear (or its axis word).
                EngineResolvedAction asked = Oracle(w[1], game.Game).FirstOrDefault() ?? Oracle(w[2], game.Game).FirstOrDefault();
                string askedTitle = asked == null ? w[1] : EngineActionInfo.Readable(asked.Game, asked.Id, asked.Title);
                List<Tuple<string, string, string, string, string>> cells = new List<Tuple<string, string, string, string, string>>
                {
                    // path, request, focus relation, function word for the oracle, expectation
                    Tuple.Create("assistant:create", "make a relation for " + w[3] + " in " + game.Name, string.Empty, w[3], "game"),
                    Tuple.Create("assistant:put", "put the " + w[1] + " of " + game.Name + " into Landing Gear", string.Empty, w[1], "game"),
                    Tuple.Create("assistant:expand", "expand " + expand + " with " + game.Name, string.Empty, "roll", "game"),
                    Tuple.Create("assistant:focus", "add the " + w[0] + " of " + game.Alias, string.Empty, w[0], "game"),
                    Tuple.Create("assistant:ask", "what does " + askedTitle + " do in " + game.Name, string.Empty, w[1], "answer"),
                    Tuple.Create("plan:create", "make a relation for " + w[3] + " in " + game.Name, string.Empty, w[3], "game"),
                    Tuple.Create("plan:put", "put the " + w[1] + " of " + game.Alias + " into Landing Gear", string.Empty, w[1], "game"),
                    Tuple.Create("plan:expand", "expand " + expand + " with " + game.Name, string.Empty, "roll", "game"),
                    Tuple.Create("plan:edit", "add " + game.Name, focus, "landing gear toggle", "game"),
                    Tuple.Create("plan:ask", "what does " + askedTitle + " do in " + game.Name, string.Empty, w[1], "answer"),
                    Tuple.Create("describe", w[0] + " in " + game.Name, string.Empty, w[0], "game"),
                    Tuple.Create("describe:alias", w[1] + " in " + game.Alias, string.Empty, w[1], "game"),
                    Tuple.Create("chat", "also " + game.Name, focus, "landing gear toggle", "game"),
                    Tuple.Create("llm:assistant", "add the " + w[1] + " of " + game.Name + " to " + focus, string.Empty, w[1], "context"),
                    Tuple.Create("llm:plan", "make a relation for " + w[3] + " in " + game.Name, string.Empty, w[3], "context"),
                    Tuple.Create("llm:describe", w[1] + " in " + game.Name, string.Empty, w[1], "context"),
                    Tuple.Create("llm:fill", expand, string.Empty, "roll", "context")
                };
                foreach (Tuple<string, string, string, string, string> cell in cells)
                {
                    if (!on(cell.Item1))
                        continue;
                    EngineAiMatrixCell row = RunCell(game, cell.Item1, cell.Item2, cell.Item1 == "assistant:focus" ? FocusFor(w[0]) : cell.Item3, cell.Item4, cell.Item5, askedTitle, actions);
                    result.Cells.Add(row);
                }
            }
            // The whole profile: every game's gear into one relation.
            foreach (string path in new[] { "assistant:all", "plan:all" })
            {
                if (!on(path))
                    continue;
                GameCase all = new GameCase { Game = "*", Name = "every game" };
                result.Cells.Add(RunCell(all, path, "put the gear of every game into Gear", string.Empty, "landing gear", "all", string.Empty, 0));
            }
            foreach (IGrouping<string, EngineAiMatrixCell> path in result.Cells.GroupBy(c => c.Path))
            {
                result.Paths.Add(new EngineAiMatrixCount
                {
                    Path = path.Key, Cells = path.Count(), Pass = path.Count(c => c.Pass),
                    Failures = path.Where(c => !c.Pass).GroupBy(c => c.Verdict).ToDictionary(g => g.Key, g => g.Count())
                });
            }
            result.Total = result.Cells.Count;
            result.Pass = result.Cells.Count(c => c.Pass);
            result.Examples = Math.Max(0, examples);
            result.Warnings.AddRange(warnings.Distinct().Where(x => !x.Contains("action catalog not found")).Take(5));
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        // The relation the focus cell has open: the button relation of that function (FlapsDown has a direction, so the
        // landing gear toggle is the neutral one).
        private static string FocusFor(string word)
        {
            return "Gear TOGGLE";
        }

        internal static List<EngineResolvedAction> Oracle(string word, string game)
        {
            if (game == "*")
                return new List<EngineResolvedAction>();
            EngineResolveResult resolved = EngineRelationFunctions.Resolve(word, new List<string> { game }, null, null, 0);
            return resolved.Games.SelectMany(g => g.Actions).Where(a => string.Equals(a.Game, game, StringComparison.OrdinalIgnoreCase) && !a.Planes.Any(p => p.EndsWith("_easy", StringComparison.OrdinalIgnoreCase) && a.Planes.Count == 1)).ToList();
        }

        private static EngineAiMatrixCell RunCell(GameCase game, string path, string request, string focus, string word, string expect, string askedTitle, int catalogActions)
        {
            EngineAiMatrixCell row = new EngineAiMatrixCell { Game = game.Game, GameName = game.Name, Path = path, Request = request, Focus = focus };
            Stopwatch watch = Stopwatch.StartNew();
            Snapshot snapshot = new Snapshot();
            HashSet<string> before = Snapshot.Active();
            Found found = new Found();
            try
            {
                string kind = path.Split(':')[0];
                if (path.StartsWith("llm:", StringComparison.Ordinal))
                    Context(path, game, request, found);
                else if (kind == "assistant")
                    Assistant(request, focus, found);
                else if (kind == "plan")
                    Plan(request, focus, found);
                else if (kind == "describe")
                    Describe(request, found);
                else if (kind == "chat")
                    Chat(request, focus, found);
                if (!path.StartsWith("llm:", StringComparison.Ordinal))
                {
                    foreach (string added in Snapshot.Active().Where(a => !before.Contains(a)))
                    {
                        string[] parts = added.Split('\t');
                        EngineRelationSuggest.Command command = EngineIdMeanings.Find(parts[0], parts[1], parts[2]);
                        found.Add(parts[0], parts[1], command != null ? command.Title : parts[1]);
                    }
                }
            }
            catch (Exception ex)
            {
                found.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                snapshot.Restore();
            }
            watch.Stop();
            row.DurationMs = watch.ElapsedMilliseconds;
            Judge(row, game, found, word, expect, askedTitle, catalogActions);
            return row;
        }

        private static void Assistant(string request, string focus, Found found)
        {
            EngineRelationAssistantResult result = EngineRelationAssistant.Run(new EngineRelationAssistant.Options { Message = request, Relation = focus, LlmMode = "off" });
            foreach (EngineRelationAssistantChange change in result.Changes.Where(c => c.Kind == "added"))
                found.Add(change.Game, change.Id, change.Title);
            found.Say(result.Reply);
            found.Say(result.Summary == "Nothing changed." ? string.Empty : result.Summary);
            foreach (string error in result.Errors)
                found.Say(error);
            found.NeedsModel = result.NeedsModel;
        }

        private static void Plan(string request, string focus, Found found)
        {
            EngineRelationPlanAsk ask = EngineRelationPlan.Ask(request, focus, "off");
            found.Say(ask.Reply);
            foreach (EngineRelationPlanStep step in ask.Steps)
            {
                found.Say(step.Text);
                found.Say(step.Error);
                foreach (string warning in step.Warnings)
                    found.Say(warning);
                foreach (string detail in step.Details)
                    found.Say(detail);
                if (step.Fill != null)
                {
                    found.Say(step.Fill.Summary);
                    foreach (EngineFillAction action in step.Fill.Relations.SelectMany(r => r.Actions))
                    {
                        found.Add(action.Game, action.Id, action.Title);
                        // As the preview shows it: the title and the aircraft (DCS) or the game's name.
                        found.Say(action.Title + " (" + (action.Game == "DCS" ? action.Plane : EngineGameWords.Label(action.Game)) + ")");
                    }
                }
            }
            foreach (string error in ask.Errors)
                found.Say(error);
            found.NeedsModel = ask.Errors.Any(e => e.IndexOf("local AI model", StringComparison.OrdinalIgnoreCase) >= 0) && ask.Steps.Count == 0 && ask.Reply.Length == 0;
        }

        private static void Describe(string request, Found found)
        {
            EngineNlGroupResult result = EngineNlGroup.Run(new EngineNlGroup.Options { Request = request, DryRun = true, LlmMode = "off" });
            foreach (EngineNlGroupProposal proposal in result.Proposals)
            {
                found.Say(proposal.Name);
                foreach (EngineNlGroupNode node in proposal.Nodes.Where(n => n.Planes.Count > 0))
                {
                    EngineRelationSuggest.Command command = EngineIdMeanings.Find(node.Game, node.Id, node.Planes[0]);
                    found.Add(node.Game, node.Id, command != null ? command.Title : node.Id);
                    foreach (string explanation in node.Explanations.Take(3))
                        found.Say(explanation);
                }
                foreach (string missing in proposal.Missing.Take(3))
                    found.Say("Not in: " + missing);
            }
            foreach (string error in result.Errors)
                found.Say(error);
        }

        private static void Chat(string request, string focus, Found found)
        {
            EngineRelationChatResult result = EngineRelationChat.Run(new EngineRelationChat.Options { Relation = focus, Message = request, LlmMode = "off" });
            foreach (EngineRelationChatChange change in result.Add)
                found.Add(change.Game, change.Id, change.Title);
            found.Say(result.Note);
            foreach (string error in result.Errors)
                found.Say(error);
        }

        // What the local model would be shown (no model runs): the context must name the game and show its actions.
        private static void Context(string path, GameCase game, string request, Found found)
        {
            string text = string.Empty;
            switch (path)
            {
                case "llm:assistant":
                    text = EngineRelationAssistant.Run(new EngineRelationAssistant.Options { Message = request, ShowPrompt = true, LlmMode = "auto" }).Llm.Prompt;
                    break;
                case "llm:plan":
                    text = EngineRelationPlan.SystemPrompt + "\n" + EngineRelationPlan.Summary(new EngineRelationPlanAsk { Request = request }, 6000);
                    break;
                case "llm:describe":
                    text = EngineNlGroup.Run(new EngineNlGroup.Options { Request = request, DryRun = true, LlmMode = "off", ShowPrompt = true }).Llm.Prompt;
                    break;
                case "llm:fill":
                    text = EngineRelationFill.ModelPrompt(request, (game.Game == "DCS" ? "DCS|F-16C_50" : game.Game + "|" + game.Game));
                    break;
            }
            found.Say(text);
        }

        // ------------------------------------------------------------------------------------------------------
        // The verdict
        // ------------------------------------------------------------------------------------------------------

        private static readonly Regex[] RawPatterns =
        {
            new Regex(@"\bd\d+pnil\w*", RegexOptions.Compiled),
            new Regex(@"\bKEY_[A-Z0-9_]+", RegexOptions.Compiled),
            new Regex(@"\bsim/[\w/]+", RegexOptions.Compiled),
            new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){2,}\b", RegexOptions.Compiled),
            new Regex(@"\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+){1,}\b", RegexOptions.Compiled),
            new Regex(@"\b[a-z_]+\|[a-z_]+\b", RegexOptions.Compiled)
        };

        internal static string RawId(string text, IEnumerable<EngineAiMatrixAction> actions)
        {
            string t = text ?? string.Empty;
            // Relation names are the user's own words.
            foreach (string name in InternalDataManagement.AllRelations.Keys.Where(k => k.Length >= 3).OrderByDescending(k => k.Length))
                t = t.Replace(name, " ");
            foreach (EngineAiMatrixAction action in actions)
            {
                if (action.Id.Length >= 4 && !EngineActionInfo.IsReadable(action.Game, action.Id, action.Id) && t.IndexOf(action.Id, StringComparison.Ordinal) >= 0)
                    return action.Id;
            }
            // A game's internal id instead of its name ("XPlane12", "StarCitizen", "IL2Game").
            foreach (string game in GameIds())
            {
                if (Regex.IsMatch(t, @"(?<![\w-])" + Regex.Escape(game) + @"(?![\w-])"))
                    return game;
            }
            foreach (Regex pattern in RawPatterns)
            {
                Match m = pattern.Match(t);
                if (m.Success)
                    return m.Value;
            }
            return string.Empty;
        }

        private static List<string> gameIds;

        // Game ids that are not words a user reads (DCS and X4 are their own names).
        private static List<string> GameIds()
        {
            if (gameIds == null)
            {
                List<string> ids = new List<string> { "IL2Game", "StarCitizen" };
                ids.AddRange(GameRegistry.All.Select(g => g.GameId));
                gameIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => Regex.IsMatch(id, @"[a-z][A-Z]|[A-Za-z]\d|\d[A-Za-z]") && id != "X4").ToList();
            }
            return gameIds;
        }

        private static void Judge(EngineAiMatrixCell row, GameCase game, Found found, string word, string expect, string askedTitle, int catalogActions)
        {
            string text = found.Text.ToString();
            row.Answer = Regex.Replace(text, @"\s+", " ").Trim();
            if (row.Answer.Length > 400)
                row.Answer = row.Answer.Substring(0, 397) + "...";
            row.Actions = found.Actions.Count;
            row.GameActions = found.Actions.Count(a => string.Equals(a.Game, game.Game, StringComparison.OrdinalIgnoreCase));
            row.OtherGames = found.Actions.Where(a => !string.Equals(a.Game, game.Game, StringComparison.OrdinalIgnoreCase)).Select(a => a.Game).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();
            row.Sample = found.Actions.Where(a => game.Game == "*" || string.Equals(a.Game, game.Game, StringComparison.OrdinalIgnoreCase)).Select(a => a.Title).Distinct().Take(4).ToList();
            List<EngineResolvedAction> oracle = Oracle(word, game.Game);
            // A relation of one kind takes only that kind: Roll is an axis, Gear TOGGLE a button (Hell Let Loose has its
            // roll only as buttons, which an axis relation cannot take).
            if (row.Path.EndsWith(":expand", StringComparison.Ordinal) || row.Path == "llm:fill")
                oracle = oracle.Where(a => a.IsAxis).ToList();
            else if (row.Path == "plan:edit" || row.Path == "chat")
                oracle = oracle.Where(a => !a.IsAxis).ToList();
            row.GameHasIt = game.Game == "*" || oracle.Count > 0;
            if (found.Error.Length > 0)
            {
                row.Verdict = "error";
                row.Answer = found.Error;
                return;
            }
            if (expect == "context")
            {
                // What JoyPro adds: the request itself (the user's words) does not count.
                if (row.Path != "llm:fill")
                    text = text.Replace(row.Request, " ");
                List<string> titles = oracle.Take(30).Select(a => EngineActionInfo.Readable(a.Game, a.Id, a.Title)).ToList();
                bool named = Mentions(text, game);
                bool shown = titles.Count == 0 || titles.Any(t => t.Length > 2 && text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                bool dcsOnly = game.Game != "DCS" && Regex.IsMatch(text, @"action of a DCS aircraft|DCS aircraft only", RegexOptions.IgnoreCase);
                row.Pass = named && shown && !dcsOnly;
                row.Verdict = row.Pass ? "ok" : !named ? "game not in the model's context" : dcsOnly ? "model told DCS only" : "game's actions not in the context";
                row.Answer = named ? (shown ? "context names the game and shows its actions" : "context names the game but none of its actions") : "context does not name the game";
                // JOYPRO_AI_MATRIX_CONTEXT=1: the whole context in the answer (to see what the model gets).
                if (Environment.GetEnvironmentVariable("JOYPRO_AI_MATRIX_CONTEXT") == "1")
                    row.Answer += " | " + Regex.Replace(text, @"\s+", " ").Trim();
                return;
            }
            string raw = RawId(text, found.Actions);
            if (expect == "answer")
            {
                bool hasGame = Mentions(text, game);
                bool hasTitle = askedTitle.Length == 0 || text.IndexOf(askedTitle, StringComparison.OrdinalIgnoreCase) >= 0;
                row.Pass = !found.NeedsModel && hasGame && hasTitle && raw.Length == 0 && found.Actions.Count == 0;
                row.Verdict = row.Pass ? "ok" : found.NeedsModel || text.IndexOf("local AI model", StringComparison.OrdinalIgnoreCase) >= 0 ? "needs the model" : found.Actions.Count > 0 ? "changed relations instead of answering" :
                    raw.Length > 0 ? "raw id: " + raw : !hasGame ? "no answer about the game" : "does not name the action";
                return;
            }
            if (expect == "all")
            {
                int games = found.Actions.Select(a => a.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                row.GameActions = games;
                row.Pass = games >= 8 && raw.Length == 0;
                row.Verdict = row.Pass ? "ok" : raw.Length > 0 ? "raw id: " + raw : games == 0 ? "empty" : "only " + games.ToString(CultureInfo.InvariantCulture) + " games";
                return;
            }
            if (row.OtherGames.Count > 0)
            {
                row.Verdict = "other games (" + string.Join(", ", row.OtherGames.Take(4)) + (row.OtherGames.Count > 4 ? " +" + (row.OtherGames.Count - 4).ToString(CultureInfo.InvariantCulture) : string.Empty) + ")";
                return;
            }
            if (raw.Length > 0)
            {
                row.Verdict = "raw id: " + raw;
                return;
            }
            if (row.GameHasIt && row.GameActions == 0)
            {
                // A relation that already holds all of it is fine when the answer says so.
                bool already = Regex.IsMatch(text, @"already|nothing new|all of them were in|has it already|kept in|stays in", RegexOptions.IgnoreCase);
                row.Pass = already && !found.NeedsModel;
                row.Verdict = row.Pass ? "ok (already there)" : found.NeedsModel || text.IndexOf("local AI model", StringComparison.OrdinalIgnoreCase) >= 0 ? "needs the model" : text.Trim().Length == 0 ? "empty" : "nothing found";
                return;
            }
            if (!row.GameHasIt)
            {
                row.Pass = row.GameActions == 0 && text.Trim().Length > 0 && !found.NeedsModel;
                row.Verdict = row.Pass ? "ok (game has none)" : found.NeedsModel ? "needs the model" : "no answer";
                return;
            }
            row.Pass = true;
            row.Verdict = "ok";
        }

        private static bool Mentions(string text, GameCase game)
        {
            List<string> names = new List<string> { game.Name, EngineRelationAssistantGameNames.Label(game.Game) };
            if (!GameIds().Contains(game.Game))
                names.Add(game.Game);
            IGameIntegration integration;
            if (GameRegistry.TryResolve(game.Game, out integration))
                names.Add(Regex.Replace(integration.DisplayName, @"\s*\([^)]*\)", string.Empty));
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string ToText(EngineAiMatrixResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("AI matrix: " + result.Pass.ToString(CultureInfo.InvariantCulture) + " of " + result.Total.ToString(CultureInfo.InvariantCulture) + " cells answered sensibly (" +
                          (result.ElapsedMs / 1000).ToString(CultureInfo.InvariantCulture) + " s)");
            foreach (EngineAiMatrixCount count in result.Paths)
            {
                sb.AppendLine("  " + count.Path.PadRight(18) + " " + count.Pass.ToString(CultureInfo.InvariantCulture).PadLeft(3) + "/" + count.Cells.ToString(CultureInfo.InvariantCulture) +
                              (count.Failures.Count > 0 ? "  fails: " + string.Join("; ", count.Failures.OrderByDescending(f => f.Value).Select(f => f.Key + " " + f.Value.ToString(CultureInfo.InvariantCulture))) : string.Empty));
            }
            sb.AppendLine("Per game:");
            foreach (IGrouping<string, EngineAiMatrixCell> game in result.Cells.GroupBy(c => c.Game))
                sb.AppendLine("  " + game.Key.PadRight(16) + " " + game.Count(c => c.Pass).ToString(CultureInfo.InvariantCulture).PadLeft(3) + "/" + game.Count().ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Cells:");
            foreach (EngineAiMatrixCell cell in result.Cells)
            {
                sb.AppendLine((cell.Pass ? "  ok   " : "  FAIL ") + cell.Game + " | " + cell.Path + " | \"" + cell.Request + "\"" + (cell.Focus.Length > 0 ? " [" + cell.Focus + "]" : string.Empty) +
                              " -> " + cell.Verdict + " | game actions " + cell.GameActions.ToString(CultureInfo.InvariantCulture) + "/" + cell.Actions.ToString(CultureInfo.InvariantCulture) +
                              (cell.Sample.Count > 0 ? " e.g. " + string.Join("; ", cell.Sample) : string.Empty));
                if (result.Examples > 0 && cell.Answer.Length > 0)
                    sb.AppendLine("         answer: " + (cell.Answer.Length > result.Examples ? cell.Answer.Substring(0, result.Examples) + "..." : cell.Answer));
            }
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }

    public sealed class EngineAiMatrixAction
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
    }

    public sealed class EngineAiMatrixCell
    {
        public string Game { get; set; }
        public string GameName { get; set; }
        public string Path { get; set; }
        public string Request { get; set; }
        public string Focus { get; set; }
        public bool Pass { get; set; }
        public string Verdict { get; set; }
        public bool GameHasIt { get; set; }
        public int Actions { get; set; }
        public int GameActions { get; set; }
        public List<string> OtherGames { get; set; }
        public List<string> Sample { get; set; }
        public string Answer { get; set; }
        public long DurationMs { get; set; }

        public EngineAiMatrixCell()
        {
            Game = GameName = Path = Request = Focus = Verdict = Answer = string.Empty;
            OtherGames = new List<string>();
            Sample = new List<string>();
        }
    }

    public sealed class EngineAiMatrixCount
    {
        public string Path { get; set; }
        public int Cells { get; set; }
        public int Pass { get; set; }
        public Dictionary<string, int> Failures { get; set; }
    }

    public sealed class EngineAiMatrixResult
    {
        public int Total { get; set; }
        public int Pass { get; set; }
        public int Examples { get; set; }
        public long ElapsedMs { get; set; }
        public List<EngineAiMatrixCount> Paths { get; set; }
        public List<EngineAiMatrixCell> Cells { get; set; }
        public List<string> Warnings { get; set; }

        public EngineAiMatrixResult()
        {
            Paths = new List<EngineAiMatrixCount>();
            Cells = new List<EngineAiMatrixCell>();
            Warnings = new List<string>();
        }
    }

    internal static partial class Program
    {
        // relations ai-matrix [--games g1|g2] [--paths p1|p2] [--examples N]: nothing is saved.
        private static int AiMatrixCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            List<string> games = SplitList(PopOptionValues(args, "--games"));
            List<string> paths = SplitList(PopOptionValues(args, "--paths"));
            string examplesRaw = PopOptionValue(args, "--examples");
            int examples = 0;
            if (examplesRaw != null && (!int.TryParse(examplesRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out examples) || examples < 0))
                return Fail("--examples takes a number of characters (0 = no answers).");
            if (args.Count > 0)
                return Fail("relations ai-matrix [--games g1|g2] [--paths p1|p2] [--examples N]");
            EngineAiMatrixResult result = EngineAiMatrix.Run(games, paths, examples);
            CommandChangedNothing = true;
            EmitPayload(textOutput ? EngineAiMatrix.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }
    }
}
