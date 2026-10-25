using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // One action of one aircraft switched on in more than one relation ("engine relations duplicates", profile validate):
    // the game then gets the same command on two inputs (DCS: the AH-64's "Force Trim/Hold Mode Switch - AL/Right" on a
    // MFBB button and on a MOZA button). The fix is to keep it in one relation and switch it off in the others
    // (docs/relation-builder.md "One action on in two relations"). Each one gets a suggestion with a confidence (0-100):
    //   - the profile history: the newest older version where only one of the relations had it on (or none: switch it
    //     off in all) - unless the version that brought the second one was the user's own edit of that relation;
    //   - else the evidence of each relation: its function (the function chip: set, named or covered) or what its name
    //     says (the function map) against the action's function, how the aircraft's meaning of the id fits the node's
    //     other aircraft, a button position ("Different functions per aircraft") as neutral; the confidence grows with
    //     the lead of the best relation (calibrated on real profiles: engine relations duplicates --evaluate);
    //   - a relation pair the user kept both before lowers it.
    public sealed class EngineDuplicateReason
    {
        // history, history-none, history-deliberate, function, name, other-function, meaning, differs, position, dismissed-pair
        public string Code { get; set; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public sealed class EngineDuplicatePlace
    {
        public string Relation { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        // stick, throttle, panel, pedals, collective, other (the device's kind; engine relations duplicates --rule device:<kind>).
        public string DeviceClass { get; set; } = string.Empty;
        // "fits": the aircraft's title of the id means what the node's other switched-on aircraft mean; "alone": the only
        // switched-on aircraft of the node; "differs": something else than the node's other aircraft.
        public string Fit { get; set; } = string.Empty;
        // "function": the relation's function is the action's; "name": its name says the action's function; "other": it
        // stands for another function; "none": it says nothing about a function.
        public string FunctionMatch { get; set; } = string.Empty;
        // A button position: the relation's aircraft use it for different functions.
        public bool Position { get; set; }
        public int Score { get; set; }
    }

    public sealed class EngineDuplicateAction
    {
        // "game|id|plane"
        public string Key { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Plane { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<EngineDuplicatePlace> Places { get; set; } = new List<EngineDuplicatePlace>();
        // The relation to keep it in (empty: no suggestion, or switch it off in all).
        public string Suggested { get; set; } = string.Empty;
        // None of the relations should have it: switch it off in all of them.
        public bool SuggestOff { get; set; }
        public int Confidence { get; set; }
        // "history", "meaning" or "" (no suggestion).
        public string SuggestionSource { get; set; } = string.Empty;
        // The history version the suggestion comes from (local time).
        public string SuggestionVersion { get; set; } = string.Empty;
        // Where it was before the second relation got it (the profile history): one relation, "-" for none, "" unknown.
        public string FirstIn { get; set; } = string.Empty;
        public List<EngineDuplicateReason> Reasons { get; set; } = new List<EngineDuplicateReason>();
        // The user keeps it in these relations on purpose ("Keep both"): no warning.
        public bool Kept { get; set; }
        public string GroupKey { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    // An aircraft as people call it: the cockpit catalog's name of the game's aircraft and its seat.
    public sealed class EngineDuplicatePlaneName
    {
        public string Plane { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        // CPG, Pilot, WSO, RIO, ... (empty: the only seat).
        public string Seat { get; set; } = string.Empty;
    }

    public sealed class EngineDuplicateGroupAction
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<string> Planes { get; set; } = new List<string>();
        public List<string> Keys { get; set; } = new List<string>();
        // "AH-64D_BLK_II (CPG and Pilot)"
        public string Aircraft { get; set; } = string.Empty;
    }

    // The duplicates of one relation pair (or set) in one game: one card.
    public sealed class EngineDuplicateGroup
    {
        public string Key { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public List<EngineDuplicatePlace> Relations { get; set; } = new List<EngineDuplicatePlace>();
        public List<EngineDuplicateGroupAction> Actions { get; set; } = new List<EngineDuplicateGroupAction>();
        public List<string> Planes { get; set; } = new List<string>();
        public List<EngineDuplicatePlaneName> PlaneNames { get; set; } = new List<EngineDuplicatePlaneName>();
        public List<string> Keys { get; set; } = new List<string>();
        public string Suggested { get; set; } = string.Empty;
        public bool SuggestOff { get; set; }
        public int Confidence { get; set; }
        public string SuggestionSource { get; set; } = string.Empty;
        public List<EngineDuplicateReason> Reasons { get; set; } = new List<EngineDuplicateReason>();
        public bool Kept { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // One change a fix makes: the action of the aircraft stays on only in Keep (Off: in none).
    public sealed class EngineDuplicateChange
    {
        public string Key { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Plane { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Keep { get; set; } = string.Empty;
        public bool Off { get; set; }
        public List<string> SwitchOffIn { get; set; } = new List<string>();
        public int Confidence { get; set; }
        // What decided it: "suggested", "name", "first", "device:<kind>", "relation:<text>", "instruction: <phrase>".
        public string Rule { get; set; } = string.Empty;
    }

    public sealed class EngineDuplicateEvaluationRow
    {
        public int Threshold { get; set; }
        public int Proposals { get; set; }
        public int Correct { get; set; }
        // Not the state before, but a bind that is right too: none of the relations had it, and it stays in the one whose
        // function is the action's and where its meaning fits ("su-25T Trim: Nose Up" kept in "Trim Nose Up").
        public int Acceptable { get; set; }
        public double Precision { get; set; }
        public double PrecisionAcceptable { get; set; }
        public int Cases { get; set; }
    }

    public sealed class EngineDuplicatesResult
    {
        public int Count { get; set; }
        public int KeptCount { get; set; }
        public int WithSuggestion { get; set; }
        public int Resolved { get; set; }
        public int SwitchedOff { get; set; }
        public bool Applied { get; set; }
        public string Rule { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;
        // The phrases the instruction was read as, and the parts nothing understood.
        public List<string> Understood { get; set; } = new List<string>();
        public List<string> NotUnderstood { get; set; } = new List<string>();
        public string Method { get; set; } = string.Empty;
        public List<EngineDuplicateGroup> Groups { get; set; } = new List<EngineDuplicateGroup>();
        public List<EngineDuplicateAction> Duplicates { get; set; } = new List<EngineDuplicateAction>();
        public List<EngineDuplicateChange> Changes { get; set; } = new List<EngineDuplicateChange>();
        public List<EngineDuplicateEvaluationRow> Evaluation { get; set; } = new List<EngineDuplicateEvaluationRow>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    internal static partial class EngineRelationDuplicates
    {
        // Off for engine relations duplicates --evaluate --no-history (the confidence without the history).
        internal static bool UseHistory = true;

        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "to", "and", "or", "for", "with", "by", "at", "is", "button", "switch", "key", "command", "axis", "mod", "w"
        };

        // Seats of a module's lists ("AH-64D_BLK_II_CPG"): shown as one aircraft with its seats.
        private static readonly Dictionary<string, string> Seats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CPG", "CPG" }, { "PLT", "Pilot" }, { "WSO", "WSO" }, { "RIO", "RIO" }, { "pilot", "Pilot" }, { "operator", "Operator" },
            { "GUN", "Gunner" }, { "LoadMaster", "Loadmaster" }, { "gunner", "Gunner" }, { "copilot", "Copilot" }, { "engineer", "Engineer" }
        };

        internal static string KeyOf(string game, string id, string plane)
        {
            return (string.IsNullOrWhiteSpace(game) ? "DCS" : game) + "|" + (id ?? string.Empty) + "|" + (plane ?? string.Empty).Trim();
        }

        internal static bool TrySplitKey(string key, out string game, out string id, out string plane)
        {
            game = id = plane = string.Empty;
            string[] parts = (key ?? string.Empty).Split('|');
            if (parts.Length < 3)
                return false;
            game = parts[0];
            plane = parts[parts.Length - 1];
            id = string.Join("|", parts.Skip(1).Take(parts.Length - 2));
            return game.Length > 0 && id.Length > 0 && plane.Length > 0;
        }

        private static string KeptEntry(string key, IEnumerable<string> relations)
        {
            return key + "|" + string.Join("\t", relations.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        }

        private static string RelationSet(string keptEntry)
        {
            int bar = keptEntry.LastIndexOf('|');
            return bar < 0 ? string.Empty : keptEntry.Substring(bar + 1);
        }

        // Every action of an aircraft that more than one relation has switched on, in a stable order; kept ones (the user
        // keeps both) only with includeKept.
        internal static List<EngineDuplicateAction> Find(bool includeKept = false)
        {
            Dictionary<string, List<KeyValuePair<Relation, RelationItem>>> owners = new Dictionary<string, List<KeyValuePair<Relation, RelationItem>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string[]> parts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null))
            {
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key))
                            continue;
                        string key = KeyOf(game, node.ID, plane.Key);
                        List<KeyValuePair<Relation, RelationItem>> list;
                        if (!owners.TryGetValue(key, out list))
                        {
                            owners[key] = list = new List<KeyValuePair<Relation, RelationItem>>();
                            parts[key] = new[] { game, node.ID, plane.Key.Trim() };
                        }
                        if (!list.Any(o => ReferenceEquals(o.Key, relation)))
                            list.Add(new KeyValuePair<Relation, RelationItem>(relation, node));
                    }
                }
            }

            HashSet<string> kept = new HashSet<string>(InternalDataManagement.KeptDuplicates ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> keptPairs = new HashSet<string>(kept.Select(RelationSet), StringComparer.OrdinalIgnoreCase);
            Context context = new Context();
            List<EngineDuplicateAction> result = new List<EngineDuplicateAction>();
            foreach (KeyValuePair<string, List<KeyValuePair<Relation, RelationItem>>> entry in owners.Where(o => o.Value.Count > 1))
            {
                string[] p = parts[entry.Key];
                EngineDuplicateAction duplicate = new EngineDuplicateAction { Key = entry.Key, Game = p[0], Id = p[1], Plane = p[2] };
                duplicate.Kept = kept.Contains(KeptEntry(entry.Key, entry.Value.Select(o => o.Key.NAME ?? string.Empty)));
                if (duplicate.Kept && !includeKept)
                    continue;
                duplicate.Title = TitleOf(p[0], p[1], p[2], entry.Value.Select(o => o.Value));
                foreach (KeyValuePair<Relation, RelationItem> owner in entry.Value)
                {
                    Bind bind = owner.Key.bind;
                    if (bind == null && InternalDataManagement.AllBinds != null)
                        InternalDataManagement.AllBinds.TryGetValue(owner.Key.NAME ?? string.Empty, out bind);
                    EngineDuplicatePlace place = new EngineDuplicatePlace
                    {
                        Relation = owner.Key.NAME ?? string.Empty,
                        Device = bind == null ? string.Empty : bind.Joystick ?? string.Empty,
                        Input = bind == null ? string.Empty : (owner.Key.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty
                    };
                    place.DeviceClass = context.DeviceClass(place.Device);
                    Score(context, place, owner.Key, owner.Value, p[0], p[2], duplicate.Reasons);
                    duplicate.Places.Add(place);
                }
                duplicate.Places = duplicate.Places.OrderBy(pl => pl.Relation, StringComparer.OrdinalIgnoreCase).ToList();
                duplicate.GroupKey = duplicate.Game + "|" + string.Join("\t", duplicate.Places.Select(pl => pl.Relation));
                SuggestByMeaning(duplicate);
                if (keptPairs.Contains(string.Join("\t", duplicate.Places.Select(pl => pl.Relation))) && duplicate.Confidence > 0)
                {
                    duplicate.Confidence = Math.Max(0, duplicate.Confidence - 25);
                    duplicate.Reasons.Add(new EngineDuplicateReason { Code = "dismissed-pair" });
                }
                result.Add(duplicate);
            }
            if (UseHistory)
                SuggestFromHistory(result);
            foreach (EngineDuplicateAction duplicate in result)
                duplicate.Message = Message(duplicate);
            return result
                .OrderBy(d => d.Game, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Plane, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Only the keys of the actions on in several relations (no evidence, no history): before and after an import.
        internal static List<string> FindKeys()
        {
            Dictionary<string, HashSet<string>> owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null))
            {
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.Where(p => p.Value && !string.IsNullOrWhiteSpace(p.Key)))
                    {
                        string key = KeyOf(node.Game, node.ID, plane.Key);
                        HashSet<string> set;
                        if (!owners.TryGetValue(key, out set))
                            owners[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(relation.NAME ?? string.Empty);
                    }
                }
            }
            HashSet<string> kept = new HashSet<string>(InternalDataManagement.KeptDuplicates ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return owners.Where(o => o.Value.Count > 1 && !kept.Contains(KeptEntry(o.Key, o.Value))).Select(o => o.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Per run: the function catalog, the function map and the relations' verdicts, device kinds.
        private sealed class Context
        {
            public EngineRelationPurpose.Index Purpose;
            public EngineFunctionMap.Map Map;
            private readonly Dictionary<string, EngineRelationPurpose.Verdict> verdicts = new Dictionary<string, EngineRelationPurpose.Verdict>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, EngineFunctionMap.Understanding> names = new Dictionary<string, EngineFunctionMap.Understanding>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Context()
            {
                try
                {
                    Purpose = EngineRelationPurpose.Load(null);
                }
                catch (Exception)
                {
                    Purpose = null;
                }
                try
                {
                    Map = EngineFunctionMap.Load(null);
                }
                catch (Exception)
                {
                    Map = null;
                }
            }

            public EngineRelationPurpose.Verdict Verdict(Relation relation)
            {
                EngineRelationPurpose.Verdict verdict;
                if (!verdicts.TryGetValue(relation.NAME ?? string.Empty, out verdict))
                {
                    try
                    {
                        verdict = Purpose == null ? new EngineRelationPurpose.Verdict() : EngineRelationPurpose.Judge(relation, Purpose);
                    }
                    catch (Exception)
                    {
                        verdict = new EngineRelationPurpose.Verdict();
                    }
                    verdicts[relation.NAME ?? string.Empty] = verdict;
                }
                return verdict;
            }

            public EngineFunctionMap.Understanding NameOf(Relation relation)
            {
                EngineFunctionMap.Understanding u;
                string name = EngineRelationPurpose.BaseName(relation.NAME);
                if (!names.TryGetValue(name, out u))
                {
                    try
                    {
                        u = Map == null ? new EngineFunctionMap.Understanding() : EngineFunctionMap.Resolve(Map, name);
                    }
                    catch (Exception)
                    {
                        u = new EngineFunctionMap.Understanding();
                    }
                    names[name] = u;
                }
                return u;
            }

            public string DeviceClass(string device)
            {
                string kind;
                if (devices.TryGetValue(device ?? string.Empty, out kind))
                    return kind;
                kind = DeviceClassOf(device);
                devices[device ?? string.Empty] = kind;
                return kind;
            }
        }

        // stick, throttle, panel, pedals, collective or other: the device catalog's kind of the device, else its name.
        internal static string DeviceClassOf(string device)
        {
            string name = ShortDevice(device);
            if (name.Length == 0)
                return "other";
            string kind = string.Empty;
            try
            {
                KbDevice model = EngineDeviceKb.Search(name, null).FirstOrDefault(k => !k.IsGrip);
                kind = model == null ? string.Empty : model.Kind ?? string.Empty;
            }
            catch (Exception)
            {
                kind = string.Empty;
            }
            string lower = (kind.Length > 0 ? kind : name).ToLowerInvariant();
            if (lower.Contains("throttle"))
                return "throttle";
            if (lower.Contains("collective"))
                return "collective";
            if (lower.Contains("pedal") || lower.Contains("rudder"))
                return "pedals";
            if (lower.Contains("stick") || lower.Contains("grip") || lower == "base" || lower.Contains(" base") || lower.Contains("ffb") || lower.Contains("joystick"))
                return "stick";
            if (lower.Contains("mfd") || lower.Contains("panel") || lower.Contains("box") || lower.Contains("mfbb") || lower.Contains("button") || lower.Contains("keypad"))
                return "panel";
            return kind.Length > 0 ? kind.ToLowerInvariant() : "other";
        }

        // The aircraft's own title of the action: the game's list, else the title the node keeps, else a readable id.
        private static string TitleOf(string game, string id, string plane, IEnumerable<RelationItem> nodes)
        {
            try
            {
                EngineRelationSuggest.Command command = EngineIdMeanings.Find(game, id, plane);
                if (command != null && !string.IsNullOrWhiteSpace(command.Title))
                    return EngineReadableNames.Title(game, command.Title);
            }
            catch (Exception)
            {
            }
            foreach (RelationItem node in nodes)
            {
                DCSInput input = (node.AllInputs ?? new DCSInput[0]).FirstOrDefault(i => i != null && string.Equals(i.Plane, plane, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Title));
                if (input != null)
                    return EngineReadableNames.Title(game, input.Title);
                OtherGameInput other = (node.OtherInputs ?? new OtherGameInput[0]).FirstOrDefault(i => i != null && !string.IsNullOrWhiteSpace(i.Title));
                if (other != null)
                    return EngineReadableNames.Title(game, other.Title);
            }
            return "an action without a title in the game's list";
        }

        // The evidence that the action of the aircraft belongs in this relation.
        private static void Score(Context context, EngineDuplicatePlace place, Relation relation, RelationItem node, string game, string plane, List<EngineDuplicateReason> reasons)
        {
            string g = EngineRelationSuggest.GameKey(game);
            int score = 0;
            // The node's meaning: the aircraft's title of the id against the node's other switched-on aircraft.
            place.Fit = "alone";
            List<string> others = node.AIRCRAFT.Where(a => a.Value && !string.Equals(a.Key, plane, StringComparison.OrdinalIgnoreCase)).Select(a => a.Key).ToList();
            EngineRelationSuggest.Command own = null;
            try
            {
                own = EngineIdMeanings.Find(game, node.ID, plane);
                List<EngineRelationSuggest.Command> references = others.Select(o => EngineIdMeanings.Find(game, node.ID, o)).Where(c => c != null).ToList();
                if (own != null && references.Count > 0 && g == "DCS")
                {
                    List<EngineIdMeanings.Cluster> clusters = EngineIdMeanings.Clusters(references);
                    EngineIdMeanings.Cluster main = EngineIdMeanings.MainCluster(clusters, relation.NAME);
                    string note;
                    bool fits = main != null && main.Head != null && main.Head.Sample != null &&
                                (EngineIdMeanings.Compare(main.Head.Sample, own, out note) != EngineIdMeanings.Kind.Different ||
                                 (main.Head.Planes.Count > 0 && EngineIdMeanings.SameFunction(game, node.ID, main.Head.Planes[0], plane)));
                    place.Fit = fits ? "fits" : "differs";
                }
                else if (others.Count > 0)
                {
                    place.Fit = "fits";
                }
            }
            catch (Exception)
            {
                place.Fit = others.Count > 0 ? "fits" : "alone";
            }
            // Alone in its node: the load correction never makes such a node (it only adds aircraft to nodes that have
            // others), so this is where the user or the game's own binds put it.
            score += place.Fit == "alone" ? 6 : place.Fit == "fits" ? 1 : -2;
            if (place.Fit == "differs")
                reasons.Add(new EngineDuplicateReason { Code = "differs", Relation = place.Relation });

            // The relation's function (the chip) or what its name says, against the action's function.
            place.FunctionMatch = "none";
            EngineRelationPurpose.Verdict verdict = context.Verdict(relation);
            place.Position = verdict.IsMixed;
            string catalogPlane = g == "DCS" ? plane : g;
            if (verdict.HasFunction && context.Purpose != null)
            {
                bool same = context.Purpose.Of(g, node.ID, catalogPlane).Any(s => string.Equals(s, verdict.Slot.Id, StringComparison.OrdinalIgnoreCase));
                place.FunctionMatch = same ? "function" : "other";
            }
            // A button position's name says where it is, not what it does ("Stick Thumb Hat DOWN"): no name reading.
            if (place.FunctionMatch != "function" && !place.Position && context.Map != null)
            {
                EngineFunctionMap.Understanding name = context.NameOf(relation);
                EngineFunctionMap.Entry entry;
                List<EngineFunctionMap.Resolved> named = name.Functions.Where(f => !f.Related && f.Function != null).ToList();
                if (named.Count > 0 && context.Map.ByKey.TryGetValue(g + "|" + EngineRelationSuggest.NodeId(g, node.ID) + "|" + catalogPlane, out entry))
                {
                    bool matches = named.Any(f => entry.Functions.Any(a => a.Function != null && a.Weight >= 0.5 &&
                        string.Equals(a.Function.Id, f.Function.Id, StringComparison.OrdinalIgnoreCase) &&
                        EngineFunctionMap.DirCompatible(f.Dir.Length > 0 ? f.Dir : name.Dir, a.Dir, relation.ISAXIS)));
                    if (matches)
                        place.FunctionMatch = "name";
                    else if (place.FunctionMatch == "none")
                        place.FunctionMatch = "other";
                }
            }
            if (place.FunctionMatch == "function")
                score += 3;
            else if (place.FunctionMatch == "name")
                score += 2;
            else if (place.FunctionMatch == "other")
                score -= 2;
            if (place.FunctionMatch == "function" || place.FunctionMatch == "name")
                reasons.Add(new EngineDuplicateReason { Code = place.FunctionMatch, Relation = place.Relation });
            else if (place.FunctionMatch == "other")
                reasons.Add(new EngineDuplicateReason { Code = "other-function", Relation = place.Relation });
            // "a button position": said of button relations only (an axis relation of mixed functions is no button).
            if (place.Position && !relation.ISAXIS)
                reasons.Add(new EngineDuplicateReason { Code = "position", Relation = place.Relation });
            // Other actions of the relation for this aircraft: the relation was made for it.
            if (relation.NODES.Any(n => n != null && !ReferenceEquals(n, node) && n.AIRCRAFT != null && n.AIRCRAFT.TryGetValue(plane, out bool on) && on))
                score += 1;
            if (own != null)
                score += Math.Min(1, SharedWords(relation.NAME, own.Title));
            place.Score = score;
        }

        private static int SharedWords(string name, string title)
        {
            HashSet<string> a = Words(name);
            HashSet<string> b = Words(title);
            return a.Count(b.Contains);
        }

        internal static HashSet<string> Words(string text)
        {
            return new HashSet<string>(EngineFunctionMap.Tokenize(text ?? string.Empty).Where(t => t.Length > 1 && !Filler.Contains(t) && !t.All(char.IsDigit))
                .Select(EngineFunctionMap.Stem), StringComparer.Ordinal);
        }

        // Confidence by the lead of the best relation (engine relations duplicates --evaluate on real profiles): without
        // the history at most 80, except the clearest case (alone in its node there, meaning something else in the
        // others' nodes).
        internal static int ConfidenceOfLead(int lead)
        {
            if (lead >= 6)
                return 80;
            if (lead >= 4)
                return 70;
            if (lead >= 2)
                return 60;
            return 0;
        }

        // The suggestion from the evidence of the relations.
        private static void SuggestByMeaning(EngineDuplicateAction duplicate)
        {
            List<EngineDuplicatePlace> ranked = duplicate.Places.OrderByDescending(p => p.Score).ToList();
            if (ranked.Count < 2)
                return;
            // Nowhere at home: in no node alone, every relation stands for another function or means something else there.
            if (ranked.All(p => p.Fit != "alone" && (p.Fit == "differs" || p.FunctionMatch == "other")))
            {
                duplicate.SuggestOff = true;
                duplicate.Confidence = ranked.All(p => p.Fit == "differs" && p.FunctionMatch == "other") ? 60 : 50;
                duplicate.SuggestionSource = "meaning";
                return;
            }
            int confidence = ConfidenceOfLead(ranked[0].Score - ranked[1].Score);
            if (ranked[0].Fit == "alone" && ranked.Skip(1).All(p => p.Fit == "differs"))
                confidence = 92;
            else if (ranked[0].Fit == "alone" && ranked.Skip(1).All(p => p.Fit != "alone") && confidence > 0)
                confidence = Math.Max(confidence, 85);
            if (confidence <= 0)
                return;
            duplicate.Suggested = ranked[0].Relation;
            duplicate.Confidence = confidence;
            duplicate.SuggestionSource = "meaning";
            if (ranked[0].Fit != "differs" && ranked[1].Fit == "differs")
                duplicate.Reasons.Add(new EngineDuplicateReason { Code = "meaning", Relation = ranked[0].Relation });
        }

        // How many older versions of the profile the history suggestion reads at most.
        private const int HistoryVersions = 30;

        // The profile history knows how it was before: going back from the newest version, the first one where only
        // one of the relations had the action of the aircraft switched on is the relation it belongs to; one where all
        // of them had the node but none had it on means it was switched on by mistake everywhere (switch it off). When
        // the version that brought it into a second relation was the user's own edit of that relation, the newer place
        // is what the user wants: no history suggestion then (only the reason).
        private static void SuggestFromHistory(List<EngineDuplicateAction> duplicates)
        {
            string path = EngineProfileHistory.ProfilePath;
            if (duplicates.Count == 0 || string.IsNullOrWhiteSpace(path) || !JoyPro.History.ProfileHistory.Enabled)
                return;
            JoyPro.History.ProfileHistoryLog log;
            try
            {
                log = JoyPro.History.ProfileHistory.Read(path);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Duplicates", "No profile history to compare with: " + ex.Message);
                return;
            }
            List<EngineDuplicateAction> pending = duplicates.ToList();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int read = 0;
            // The version read before the current one (newer): the one that brought the duplicate.
            JoyPro.History.ProfileVersion newer = null;
            foreach (JoyPro.History.ProfileVersion version in log.Versions.OrderByDescending(v => v.Id))
            {
                if (pending.Count == 0 || read >= HistoryVersions)
                    break;
                if (string.IsNullOrWhiteSpace(version.Hash) || !seen.Add(version.Hash))
                    continue;
                Pr0file old;
                try
                {
                    old = InternalDataManagement.ReadProfileBytes(JoyPro.History.ProfileHistory.ReadContent(path, version.Id));
                }
                catch (Exception ex)
                {
                    JoyPro.Logging.JoyProLog.Debug("Duplicates", "History version " + version.Id.ToString(CultureInfo.InvariantCulture) + " not readable: " + ex.Message);
                    continue;
                }
                read++;
                if (old == null || old.Relations == null)
                    continue;
                Dictionary<string, HashSet<string>> active = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, HashSet<string>> nodes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Relation> entry in old.Relations)
                {
                    Relation relation = entry.Value;
                    if (relation == null)
                        continue;
                    string name = string.IsNullOrWhiteSpace(relation.NAME) ? entry.Key ?? string.Empty : relation.NAME;
                    HashSet<string> own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    nodes[name] = own;
                    foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                    {
                        if (node == null || string.IsNullOrWhiteSpace(node.ID))
                            continue;
                        string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                        own.Add(game + "|" + node.ID);
                        foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT ?? new Dictionary<string, bool>())
                        {
                            if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key))
                                continue;
                            string key = KeyOf(game, node.ID, plane.Key);
                            HashSet<string> owners;
                            if (!active.TryGetValue(key, out owners))
                                active[key] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            owners.Add(name);
                        }
                    }
                }
                string when = JoyPro.History.ProfileHistory.FormatLocal(version.Utc);
                foreach (EngineDuplicateAction duplicate in pending.ToList())
                {
                    HashSet<string> owners;
                    active.TryGetValue(duplicate.Key, out owners);
                    List<string> had = duplicate.Places.Select(p => p.Relation).Where(r => owners != null && owners.Contains(r)).ToList();
                    if (had.Count >= 2)
                        continue;
                    pending.Remove(duplicate);
                    HashSet<string> nodesOf;
                    bool allHadNode = duplicate.Places.All(p => nodes.TryGetValue(p.Relation, out nodesOf) && nodesOf.Contains(duplicate.Game + "|" + duplicate.Id));
                    if (had.Count == 0 && !allHadNode)
                        continue;
                    duplicate.FirstIn = had.Count == 1 ? had[0] : "-";
                    // The newer version was the user's edit of a relation that got it: that was wanted.
                    List<string> gained = duplicate.Places.Select(p => p.Relation).Where(r => !had.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                    string mine = newer == null ? null : DeliberateEdit(newer, gained);
                    if (mine != null)
                    {
                        duplicate.Reasons.Add(new EngineDuplicateReason { Code = "history-deliberate", Relation = mine, Detail = JoyPro.History.ProfileHistory.FormatLocal(newer.Utc) });
                        continue;
                    }
                    duplicate.SuggestionSource = "history";
                    duplicate.SuggestionVersion = when;
                    if (had.Count == 1)
                    {
                        duplicate.Suggested = had[0];
                        duplicate.SuggestOff = false;
                        duplicate.Confidence = 96;
                        duplicate.Reasons.Insert(0, new EngineDuplicateReason { Code = "history", Relation = had[0], Detail = when });
                    }
                    else
                    {
                        duplicate.Suggested = string.Empty;
                        duplicate.SuggestOff = true;
                        duplicate.Confidence = 94;
                        duplicate.Reasons.Insert(0, new EngineDuplicateReason { Code = "history-none", Detail = when });
                    }
                }
                newer = version;
            }
        }

        private static readonly string[] BulkLabels = { "import", "taking over", "before", "open", "auto ", "restore", "undo", "redo", "lay out", "setup", "resolving" };

        // The relation the version's change was the user's own edit of, among those that got the action then (null:
        // none; an import, a take-over or a correction brought it). The label of an edit names its relation ("Changing
        // the aircraft of 'X'"); an engine command without a note names it in a small summary ("Relation 'X': ...").
        private static string DeliberateEdit(JoyPro.History.ProfileVersion version, List<string> gained)
        {
            string label = version.Label ?? string.Empty;
            string named = gained.FirstOrDefault(r => label.IndexOf("'" + r + "'", StringComparison.OrdinalIgnoreCase) >= 0);
            if (named != null)
                return named;
            string lower = label.ToLowerInvariant();
            if (BulkLabels.Any(b => lower.StartsWith(b, StringComparison.Ordinal)) || lower.Contains("import"))
                return null;
            string summary = version.Summary ?? string.Empty;
            int relations = System.Text.RegularExpressions.Regex.Matches(summary, "Relation '").Count;
            if (relations == 0 || relations > 2)
                return null;
            return gained.FirstOrDefault(r => summary.IndexOf("'" + r + "'", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // The cards: per game and relation set, the actions with their aircraft; the suggestion when all agree.
        internal static List<EngineDuplicateGroup> Group(List<EngineDuplicateAction> duplicates)
        {
            List<EngineDuplicateGroup> groups = new List<EngineDuplicateGroup>();
            foreach (IGrouping<string, EngineDuplicateAction> set in duplicates.GroupBy(d => d.GroupKey, StringComparer.OrdinalIgnoreCase))
            {
                List<EngineDuplicateAction> members = set.ToList();
                EngineDuplicateAction first = members[0];
                EngineDuplicateGroup group = new EngineDuplicateGroup
                {
                    Key = set.Key,
                    Game = first.Game,
                    Kept = members.All(m => m.Kept),
                    Keys = members.Select(m => m.Key).ToList(),
                    Planes = members.Select(m => m.Plane).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                };
                group.PlaneNames = group.Planes.Select(p => PlaneNameOf(first.Game, p)).ToList();
                group.Relations = first.Places.Select(p => new EngineDuplicatePlace
                {
                    Relation = p.Relation, Device = p.Device, Input = p.Input, DeviceClass = p.DeviceClass, Position = p.Position,
                    FunctionMatch = members.Select(m => m.Places.First(x => x.Relation == p.Relation).FunctionMatch).GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key
                }).ToList();
                foreach (IGrouping<string, EngineDuplicateAction> action in members.GroupBy(m => m.Id + "\n" + m.Title, StringComparer.OrdinalIgnoreCase))
                {
                    List<string> planes = action.Select(a => a.Plane).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                    group.Actions.Add(new EngineDuplicateGroupAction
                    {
                        Id = action.First().Id,
                        Title = action.First().Title,
                        Planes = planes,
                        Keys = action.Select(a => a.Key).ToList(),
                        Aircraft = AircraftLabel(first.Game, planes)
                    });
                }
                bool sameKeep = members.All(m => m.Suggested.Length > 0 && string.Equals(m.Suggested, first.Suggested, StringComparison.OrdinalIgnoreCase));
                bool allOff = members.All(m => m.SuggestOff);
                if (sameKeep || allOff)
                {
                    group.Suggested = sameKeep ? first.Suggested : string.Empty;
                    group.SuggestOff = allOff;
                    group.Confidence = members.Min(m => m.Confidence);
                    group.SuggestionSource = members.All(m => m.SuggestionSource == "history") ? "history" : "meaning";
                }
                group.Reasons = members.SelectMany(m => m.Reasons).GroupBy(r => r.Code + "|" + r.Relation).Select(r => r.First()).ToList();
                group.Message = GroupMessage(group);
                groups.Add(group);
            }
            return groups.OrderBy(g => g.Game, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static readonly Dictionary<string, EngineDuplicatePlaneName> planeNames = new Dictionary<string, EngineDuplicatePlaneName>(StringComparer.OrdinalIgnoreCase);

        // The cockpit catalog's name of a DCS aircraft ("AH-64D_BLK_II_PLT" -> "AH-64D Block II", seat "Pilot"); else the
        // game's name without its seat ("F-14_RIO" -> "F-14", "RIO").
        internal static EngineDuplicatePlaneName PlaneNameOf(string game, string plane)
        {
            EngineDuplicatePlaneName name;
            lock (planeNames)
            {
                if (planeNames.TryGetValue((game ?? string.Empty) + "|" + plane, out name))
                    return name;
            }
            KeyValuePair<string, string> split = SplitSeat(plane);
            name = new EngineDuplicatePlaneName { Plane = plane, Name = split.Key, Seat = split.Value };
            if (string.Equals(EngineRelationSuggest.GameKey(game ?? string.Empty), "DCS", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    JoyPro.DeviceReference.CockpitVariant variant = Program.LoadCockpitCatalog().VariantOfPlane(plane);
                    if (variant != null && !string.IsNullOrWhiteSpace(variant.Name))
                    {
                        string seat = (variant.Seat ?? string.Empty).Trim();
                        string full = variant.Name.Trim();
                        int comma = full.LastIndexOf(", ", StringComparison.Ordinal);
                        name.Name = seat.Length > 0 && comma > 0 ? full.Substring(0, comma) : full;
                        if (seat.Length > 0)
                        {
                            string label;
                            name.Seat = Seats.TryGetValue(seat, out label) ? label : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(seat);
                        }
                        else if (name.Seat.Length == 0 || name.Name == split.Key)
                        {
                            name.Seat = split.Value;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            lock (planeNames)
                planeNames[(game ?? string.Empty) + "|" + plane] = name;
            return name;
        }

        // "AH-64D Block II (CPG and Pilot)", "F-14 (Pilot and RIO)", "F-16C Block 50 and F/A-18C"; other games: the game.
        internal static string AircraftLabel(string game, IEnumerable<string> planes)
        {
            if (!string.Equals(EngineRelationSuggest.GameKey(game ?? string.Empty), "DCS", StringComparison.OrdinalIgnoreCase))
                return EngineGameWords.Label(game);
            List<KeyValuePair<string, string>> split = planes.Select(p => PlaneNameOf(game, p)).Select(n => new KeyValuePair<string, string>(n.Name, n.Seat)).ToList();
            List<string> parts = new List<string>();
            foreach (IGrouping<string, KeyValuePair<string, string>> module in split.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<string> seats = module.Select(s => s.Value).Distinct().ToList();
                if (seats.Count == 1 && seats[0].Length == 0)
                {
                    parts.Add(module.Key);
                    continue;
                }
                List<string> names = seats.Select(s => s.Length == 0 ? "Pilot" : s).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                parts.Add(module.Key + " (" + JoinAnd(names) + ")");
            }
            return JoinAnd(parts);
        }

        private static KeyValuePair<string, string> SplitSeat(string plane)
        {
            int bar = (plane ?? string.Empty).LastIndexOf('_');
            string seat;
            if (bar > 0 && Seats.TryGetValue(plane.Substring(bar + 1), out seat))
                return new KeyValuePair<string, string>(plane.Substring(0, bar), seat);
            return new KeyValuePair<string, string>(plane ?? string.Empty, string.Empty);
        }

        private static string JoinAnd(List<string> items)
        {
            if (items.Count <= 1)
                return items.FirstOrDefault() ?? string.Empty;
            return string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
        }

        private static string PlaceText(EngineDuplicatePlace p)
        {
            return "'" + p.Relation + "'" + (p.Input.Length > 0 ? " (" + ShortDevice(p.Device) + " \u00B7 " + ShortInput(p.Input) + ")" : " (unbound)");
        }

        // "JOY_BTN39" -> "BTN 39", "JOY_RZ" -> "RZ", "JOY_BTN_POV1_U" -> "POV1 Up" (as on the key caps of the app).
        internal static string ShortInput(string input)
        {
            string upper = (input ?? string.Empty).Trim().ToUpperInvariant();
            System.Text.RegularExpressions.Match pov = System.Text.RegularExpressions.Regex.Match(upper, @"^JOY_BTN_POV(\d+)_(U|D|L|R|UR|UL|DR|DL)$");
            if (pov.Success)
            {
                string dir = pov.Groups[2].Value;
                string word = dir == "U" ? "Up" : dir == "D" ? "Down" : dir == "L" ? "Left" : dir == "R" ? "Right" : dir == "UR" ? "Up-Right" : dir == "UL" ? "Up-Left" : dir == "DR" ? "Down-Right" : "Down-Left";
                return "POV" + pov.Groups[1].Value + " " + word;
            }
            System.Text.RegularExpressions.Match button = System.Text.RegularExpressions.Regex.Match(upper, @"^JOY_BTN(\d+)$");
            if (button.Success)
                return "BTN " + button.Groups[1].Value;
            return upper.StartsWith("JOY_", StringComparison.Ordinal) ? upper.Substring(4).Replace('_', ' ') : (input ?? string.Empty).Trim();
        }

        private static string GameShort(string game)
        {
            return string.Equals(EngineRelationSuggest.GameKey(game ?? string.Empty), "DCS", StringComparison.OrdinalIgnoreCase) ? "DCS" : EngineGameWords.Label(game);
        }

        // "AH-64D_BLK_II (CPG and Pilot): 'Force Trim/Hold Mode Switch - AL/Right' is in 2 relations: 'Mode Button 07'
        //  (Total Controls MFBB JOY_BTN39) and 'Trim Right' (MOZA AB9 FFB Base JOY_BTN4). DCS gets two binds for it.
        //  92% sure: keep it in 'Trim Right'."
        internal static string GroupMessage(EngineDuplicateGroup group)
        {
            StringBuilder sb = new StringBuilder();
            List<string> places = group.Relations.Select(PlaceText).ToList();
            string where = JoinAnd(places);
            if (group.Actions.Count == 1)
            {
                EngineDuplicateGroupAction action = group.Actions[0];
                sb.Append(action.Aircraft).Append(": '").Append(action.Title).Append("' is in ").Append(group.Relations.Count.ToString(CultureInfo.InvariantCulture)).Append(" relations: ").Append(where).Append(".");
            }
            else
            {
                sb.Append(group.Actions.Count.ToString(CultureInfo.InvariantCulture)).Append(" actions are in ").Append(group.Relations.Count.ToString(CultureInfo.InvariantCulture)).Append(" relations: ").Append(where).Append(": ");
                sb.Append(string.Join("; ", group.Actions.Select(a => a.Aircraft + " '" + a.Title + "'"))).Append(".");
            }
            sb.Append(" ").Append(GameShort(group.Game)).Append(" gets ").Append(group.Relations.Count == 2 ? "two binds" : group.Relations.Count.ToString(CultureInfo.InvariantCulture) + " binds").Append(" for it.");
            if (group.Suggested.Length > 0)
                sb.Append(" ").Append(group.Confidence.ToString(CultureInfo.InvariantCulture)).Append("% sure: keep it in '").Append(group.Suggested).Append("'.");
            else if (group.SuggestOff)
                sb.Append(" ").Append(group.Confidence.ToString(CultureInfo.InvariantCulture)).Append("% sure: switch it off in all of them.");
            return sb.ToString();
        }

        internal static string Message(EngineDuplicateAction duplicate)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("'").Append(duplicate.Title).Append("' of ").Append(duplicate.Plane).Append(" is on in ")
              .Append(duplicate.Places.Count.ToString(CultureInfo.InvariantCulture)).Append(" relations: ");
            sb.Append(string.Join(", ", duplicate.Places.Select(p => p.Relation + (p.Input.Length > 0 ? " (" + ShortDevice(p.Device) + " \u00B7 " + ShortInput(p.Input) + ")" : " (unbound)"))));
            sb.Append(". The game gets it on each of these inputs; keep it in one relation");
            if (duplicate.Suggested.Length > 0)
                sb.Append(" (").Append(duplicate.Confidence.ToString(CultureInfo.InvariantCulture)).Append("% sure: ").Append(duplicate.Suggested)
                  .Append(duplicate.SuggestionSource == "history" ? ", the only one with it on " + duplicate.SuggestionVersion : string.Empty).Append(")");
            else if (duplicate.SuggestOff)
                sb.Append(" or switch it off in all (").Append(duplicate.Confidence.ToString(CultureInfo.InvariantCulture)).Append("% sure")
                  .Append(duplicate.SuggestionSource == "history" ? ": none of them had it on " + duplicate.SuggestionVersion : string.Empty).Append(")");
            sb.Append(".");
            return sb.ToString();
        }

        internal static string ShortDevice(string device)
        {
            string text = (device ?? string.Empty).Trim();
            int guid = text.LastIndexOf(" {", StringComparison.Ordinal);
            return guid > 0 && text.EndsWith("}", StringComparison.Ordinal) ? text.Substring(0, guid) : text;
        }

        // Keeps the action of the aircraft switched on only in the relation keep (null: in none): every other relation's
        // node of the id gets the aircraft switched off ("left out", one tick brings it back). Returns the relations changed.
        internal static List<string> KeepOnly(string game, string id, string plane, string keep)
        {
            List<string> changed = new List<string>();
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null))
            {
                if (keep != null && string.Equals(relation.NAME, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null || !string.Equals(node.ID, id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string nodeGame = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                    if (!string.Equals(nodeGame, game, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string key = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k, plane, StringComparison.OrdinalIgnoreCase));
                    if (key == null || !node.AIRCRAFT[key])
                        continue;
                    node.AIRCRAFT[key] = false;
                    if (!changed.Contains(relation.NAME ?? string.Empty))
                        changed.Add(relation.NAME ?? string.Empty);
                }
            }
            return changed;
        }

        // "Keep both": the check stops warning about these actions while exactly these relations have them.
        internal static int KeepBoth(IEnumerable<EngineDuplicateAction> duplicates)
        {
            if (InternalDataManagement.KeptDuplicates == null)
                InternalDataManagement.KeptDuplicates = new List<string>();
            int added = 0;
            foreach (EngineDuplicateAction duplicate in duplicates)
            {
                string entry = KeptEntry(duplicate.Key, duplicate.Places.Select(p => p.Relation));
                if (InternalDataManagement.KeptDuplicates.Contains(entry, StringComparer.OrdinalIgnoreCase))
                    continue;
                InternalDataManagement.KeptDuplicates.Add(entry);
                added++;
            }
            return added;
        }

        internal static string ToText(EngineDuplicatesResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Actions on in more than one relation: " + result.Count.ToString(CultureInfo.InvariantCulture) +
                          " (with a suggestion: " + result.WithSuggestion.ToString(CultureInfo.InvariantCulture) + ", kept on purpose: " + result.KeptCount.ToString(CultureInfo.InvariantCulture) + ")");
            if (result.Count > 0)
                sb.AppendLine("Resolve: engine relations duplicates --keep <relation> --keys <game|id|aircraft;...>, --off, --keep-both, --rule suggested|name|first|device:<kind>|relation:<words> [--apply], --instruction \"...\" [--apply]");
            if (result.Resolved > 0 || result.SwitchedOff > 0)
                sb.AppendLine("Resolved: " + result.Resolved.ToString(CultureInfo.InvariantCulture) + ", switched off in " + result.SwitchedOff.ToString(CultureInfo.InvariantCulture) + " relation(s)");
            if (result.Rule.Length > 0)
                sb.AppendLine("Rule: " + result.Rule + (result.Applied ? " (applied)" : " (preview)"));
            foreach (string phrase in result.Understood)
                sb.AppendLine("Understood: " + phrase);
            foreach (string phrase in result.NotUnderstood)
                sb.AppendLine("Not understood: " + phrase);
            foreach (EngineDuplicateChange change in result.Changes)
                sb.AppendLine("  * " + change.Plane + " '" + change.Title + "': " + (change.Off ? "off in all" : "keep in '" + change.Keep + "'") +
                              (change.SwitchOffIn.Count > 0 ? ", off in " + string.Join(", ", change.SwitchOffIn.Select(r => "'" + r + "'")) : string.Empty) +
                              " (" + change.Confidence.ToString(CultureInfo.InvariantCulture) + "%, " + change.Rule + ")");
            foreach (EngineDuplicateGroup group in result.Groups)
                sb.AppendLine("  - " + group.Message);
            foreach (EngineDuplicateEvaluationRow row in result.Evaluation)
                sb.AppendLine("  >= " + row.Threshold.ToString(CultureInfo.InvariantCulture) + "%: " + row.Correct.ToString(CultureInfo.InvariantCulture) + " of " +
                              row.Proposals.ToString(CultureInfo.InvariantCulture) + " as before (precision " + row.Precision.ToString("0.000", CultureInfo.InvariantCulture) + "), +" +
                              row.Acceptable.ToString(CultureInfo.InvariantCulture) + " right binds (precision " + row.PrecisionAcceptable.ToString("0.000", CultureInfo.InvariantCulture) + "), cases " + row.Cases.ToString(CultureInfo.InvariantCulture));
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }
}
