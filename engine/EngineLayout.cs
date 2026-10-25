using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // "Lay out with AI" (docs/layout-with-ai.md): the user describes in plain words how they like their controls and
    // JoyPro lays out binds, axis tuning, force feedback and the virtual joystick accordingly, with a preview of every
    // change before anything is applied and one history version to undo it.
    //   arrange ask "<text>" [--llm auto|on|off] [--games g1,g2] [--add-missing] [--forget-saved] [--out <proposal.json>]
    //   arrange apply --proposal <proposal.json> [--skip <change id>]... [--dry-run]
    //   arrange prefs [show | clear | remove <key>]
    // The phrase rules (LayoutPhrases) understand the words; the planner of Auto Assign and the Setup wizard
    // (SetupPlanner) makes the layout with the user's own layout first; the local AI model only rewrites sentences the rules
    // do not understand into phrases they do. The preferences are kept in the profile and used again next time.
    internal static class EngineLayout
    {
        public const string ProposalFormat = "joypro-layout-proposal";

        internal sealed class Change
        {
            public string Id = string.Empty;          // bind:<relation>, tuning:<relation>|<scope>, ffb:<device>|<scope>|<param>, vjoy:<game>
            public string Kind = string.Empty;        // bind, tuning, ffb, vjoy
            public string Title = string.Empty;       // the relation, the device or the game
            public string Scope = string.Empty;       // tuning / ffb scope, the modifier of a bind
            public string Old = string.Empty;
            public string New = string.Empty;
            public string OldText = string.Empty;     // Old / New / Scope in plain words (the app shows these; Old / New
            public string NewText = string.Empty;     // stay the values apply uses)
            public string ScopeText = string.Empty;
            public string Device = string.Empty;      // a bind: where it goes (the app composes it in its language)
            public string Input = string.Empty;
            public string Control = string.Empty;
            public string Label = string.Empty;
            public string Reason = string.Empty;
            public string Preference = string.Empty;  // the key of the preference that asked for it ("" = the layout itself)
            public bool NewRelation;
        }

        internal sealed class Proposal
        {
            public string Format = ProposalFormat;
            public string Text = string.Empty;
            public string Method = string.Empty;      // JoyPro's rules / the local AI model (...)
            public List<LayoutPreference> Preferences = new List<LayoutPreference>();
            public List<string> Fresh = new List<string>();          // keys of the preferences read from this request
            public List<string> NotUnderstood = new List<string>();
            public List<string> Games = new List<string>();
            public bool AddMissing;
            public string PlanFile = string.Empty;
            public List<Change> Changes = new List<Change>();
            public List<string> Notes = new List<string>();
            public List<string> Warnings = new List<string>();
            public int Binds { get { return Changes.Count(c => c.Kind == "bind"); } }
            public int Tuning { get { return Changes.Count(c => c.Kind == "tuning"); } }
            public int Ffb { get { return Changes.Count(c => c.Kind == "ffb"); } }
            public int VJoy { get { return Changes.Count(c => c.Kind == "vjoy"); } }
        }

        // ---- the preferences kept in the profile ----

        public static List<LayoutPreference> SavedPreferences()
        {
            string json = InternalDataManagement.LayoutPreferences;
            if (string.IsNullOrWhiteSpace(json))
                return new List<LayoutPreference>();
            try
            {
                return EngineSerialization.FromJson<List<LayoutPreference>>(json) ?? new List<LayoutPreference>();
            }
            catch (Exception)
            {
                return new List<LayoutPreference>();
            }
        }

        public static void SavePreferences(List<LayoutPreference> preferences)
        {
            InternalDataManagement.LayoutPreferences = preferences == null || preferences.Count == 0 ? string.Empty : EngineSerialization.ToJson(preferences);
        }

        // ---- ask ----

        public static Proposal Ask(string text, string llmMode, List<string> games, bool addMissing, bool forgetSaved, EngineSetupWizard.DeviceChoices choices, string proposalFile)
        {
            Proposal proposal = new Proposal { Text = text ?? string.Empty, AddMissing = addMissing, Method = "JoyPro's rules" };
            List<string> warnings = new List<string>();
            List<SetupInput> inputs;
            List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);

            // The games: the ones asked for, else the games of the profile's relations that have a function catalog.
            Dictionary<string, SetupFunction> functions;
            Dictionary<string, SetupAircraft> aircraft;
            List<SetupCommand> commands;
            List<string> errors = new List<string>();
            SetupCatalog.Load(EngineSetupWizard.FunctionsFolder(), errors, out functions, out aircraft, out commands);
            HashSet<string> catalogGames = new HashSet<string>(aircraft.Values.Select(a => a.Game), StringComparer.OrdinalIgnoreCase);
            proposal.Games = (games != null && games.Count > 0 ? games : ProfileGames().Where(catalogGames.Contains).ToList())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (proposal.Games.Count == 0)
                proposal.Games.AddRange(new[] { "DCS" }.Where(catalogGames.Contains));

            // Understanding: the rules first, the local AI model for the sentences they do not understand.
            Dictionary<string, string[]> gameWords = GameWords(proposal.Games.Concat(catalogGames));
            List<string> planes = aircraft.Keys.ToList();
            LayoutUnderstanding understood = LayoutPhrases.Read(proposal.Text, gameWords, planes);
            if (understood.NotUnderstood.Count > 0 && (llmMode ?? "auto") != "off")
                AskModel(understood, gameWords, planes, llmMode, proposal);
            proposal.NotUnderstood.AddRange(understood.NotUnderstood);
            proposal.Fresh.AddRange(understood.Preferences.Select(p => p.Key));
            proposal.Preferences = LayoutPhrases.Merge(forgetSaved ? new List<LayoutPreference>() : SavedPreferences(), understood.Preferences);

            // The plan: the planner of the Setup wizard and Auto Assign with the preferences as roles, options and rules.
            SetupRoles.Propose(devices);
            Dictionary<string, string> rolesBefore = devices.ToDictionary(d => d.Device, d => d.Role, StringComparer.Ordinal);
            LayoutCompiled compiled = LayoutPhrases.Compile(proposal.Preferences, devices);
            foreach (KeyValuePair<string, string> role in compiled.Roles)
            {
                SetupDevice device = devices.FirstOrDefault(d => d.Device == role.Key);
                if (device != null)
                    device.Role = role.Value;
            }
            SetupOptions options = new SetupOptions
            {
                Games = proposal.Games.ToList(),
                Modifiers = compiled.Modifiers ?? 1,
                PreferFewer = compiled.PreferFewer ?? true,
                Realism = compiled.Realism ?? 0.3,
                PhaseLayers = compiled.PhaseLayers.Length > 0 ? compiled.PhaseLayers : "auto",
                ReplaceExisting = compiled.KeepLayout != true,
                ExistingOnly = !addMissing,
                Rules = compiled.Rules,
                Priorities = compiled.Priorities,
                // "No modifiers" in the user's words is a rule: JoyPro adds none even when the buttons are too few.
                ForcedModifierLimit = compiled.Modifiers.HasValue ? compiled.Modifiers.Value : 2
            };
            proposal.Notes.AddRange(compiled.Notes);
            SetupRequest request = EngineSetupWizard.Request(devices, inputs, options, new List<string>(), warnings);

            // The user's own layout first: only the relations a preference concerns move (and unbound ones get a place).
            HashSet<string> changedRoles = new HashSet<string>(devices.Where(d => rolesBefore.ContainsKey(d.Device) && rolesBefore[d.Device] != d.Role).Select(d => d.Device), StringComparer.Ordinal);
            HashSet<string> split = Split(compiled.Rules, request, aircraft, proposal);
            HashSet<string> concerned = new HashSet<string>(split, StringComparer.OrdinalIgnoreCase);
            bool leftHandChanged = devices.Any(d => changedRoles.Contains(d.Device) && (d.Role == SetupRoles.LeftStick || rolesBefore[d.Device] == SetupRoles.LeftStick));
            foreach (SetupExistingRelation existing in request.Existing)
            {
                HashSet<string> classes = ClassesOf(existing.Nodes, aircraft);
                bool onChanged = existing.Device.Length > 0 && changedRoles.Contains(existing.Device);
                if (onChanged)
                    existing.OnSetup = true;   // a device left out now: its binds move to the devices that take its role
                if (onChanged || compiled.Rules.Any(r => Concerns(r, existing, classes)) || (leftHandChanged && classes.Count == 1 && classes.Contains(SetupClasses.Space)))
                    concerned.Add(existing.Name);
            }
            options.ReplaceOnly = compiled.FreshLayout ? null : compiled.KeepLayout == true ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : concerned;
            SetupPlan plan = SetupPlanner.Plan(request);
            proposal.Warnings.AddRange(plan.Warnings.Distinct());
            proposal.Notes.AddRange(plan.Notes.Where(n => n.StartsWith("Phase layers", StringComparison.Ordinal) || n.StartsWith("Only ", StringComparison.Ordinal) || n.StartsWith("No modifier", StringComparison.Ordinal)));

            string folder = Path.GetDirectoryName(proposalFile ?? string.Empty);
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Path.GetTempPath(), "JoyPro2", "layout-ai");
            Directory.CreateDirectory(folder);
            proposal.PlanFile = Path.Combine(folder, Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(proposalFile) ? "proposal" : proposalFile) + ".plan.json");
            File.WriteAllText(proposal.PlanFile, SetupPlanJson.Write(plan, true), new UTF8Encoding(false));

            BindChanges(plan, proposal);
            Dictionary<string, string> functionOf = request.Existing.Where(e => e.Function.Length > 0).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Function, StringComparer.OrdinalIgnoreCase);
            foreach (SetupPlanRelation relation in plan.Relations.Where(r => r.Function.Length > 0 && !functionOf.ContainsKey(r.Name)))
                functionOf[relation.Name] = relation.Function;
            TuningChanges(compiled.Tuning, plan, functionOf, aircraft, proposal);
            FfbChanges(compiled.Ffb, devices, plan, aircraft, proposal);
            VJoyChanges(compiled.VJoy, aircraft, proposal);
            if (proposal.Changes.Count == 0)
                proposal.Notes.Add("Nothing to change: your layout already fits these preferences.");
            return proposal;
        }

        private static bool Concerns(SetupRule rule, SetupExistingRelation relation, HashSet<string> classes)
        {
            if (rule.Class.Length > 0 && !(classes.Count == 1 && classes.Contains(rule.Class)))
                return false;
            if (rule.Functions.Length == 0)
                return rule.Controls.Length > 0;
            Regex regex = new Regex(rule.Functions, RegexOptions.IgnoreCase);
            return regex.IsMatch(relation.Function) || (relation.Function.Length == 0 && regex.IsMatch(relation.Name.Replace(' ', '_').ToLowerInvariant()));
        }

        private static HashSet<string> ClassesOf(IEnumerable<SetupNode> nodes, Dictionary<string, SetupAircraft> aircraft)
        {
            HashSet<string> classes = new HashSet<string>(StringComparer.Ordinal);
            foreach (SetupNode node in nodes)
            {
                SetupAircraft plane;
                if (aircraft.TryGetValue(SetupPlanner.NodeKey(node), out plane) && !plane.SharedLayer)
                    classes.Add(ClassOf(plane, aircraft));
            }
            return classes;
        }

        // A preference for one aircraft class (yaw on the left stick's twist in space games) on a relation that serves
        // several classes (one Rudder relation for jets, helicopters and Star Citizen): the commands of that class move to
        // a relation of their own ("Rudder [Spaceships]"), which the preference then places. Whole commands only: a DCS
        // command that serves aircraft of two classes stays (the note says so).
        private static HashSet<string> Split(List<SetupRule> rules, SetupRequest request, Dictionary<string, SetupAircraft> aircraft, Proposal proposal)
        {
            HashSet<string> made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupRule rule in rules.Where(r => r.Class.Length > 0 && r.Bonus > 0 && r.Controls.Length > 0))
            {
                foreach (SetupExistingRelation existing in request.Existing.ToList())
                {
                    HashSet<string> classes = ClassesOf(existing.Nodes, aircraft);
                    if (classes.Count < 2 || !classes.Contains(rule.Class) || !Concerns(new SetupRule { Functions = rule.Functions, Controls = rule.Controls }, existing, new HashSet<string>()))
                        continue;
                    Relation relation;
                    if (!InternalDataManagement.AllRelations.TryGetValue(existing.Name, out relation) || relation == null)
                        continue;
                    List<string> mixed;
                    List<RelationItem> moving = NodesOfClass(relation, rule.Class, aircraft, out mixed);
                    if (mixed.Count > 0)
                        proposal.Notes.Add("\"" + existing.Name + "\": some of its DCS commands serve " + SetupClasses.Title(rule.Class).ToLowerInvariant() + " and other aircraft at once (" + string.Join(", ", mixed.Take(3)) + "); they stay.");
                    if (moving.Count == 0 || moving.Count == relation.NODES.Count(n => n != null))
                        continue;
                    string name = existing.Name + " [" + SetupClasses.Title(rule.Class) + "]";
                    for (int i = 2; InternalDataManagement.AllRelations.ContainsKey(name) || request.Existing.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                        name = existing.Name + " [" + SetupClasses.Title(rule.Class) + " " + i.ToString(CultureInfo.InvariantCulture) + "]";
                    HashSet<string> moved = new HashSet<string>(moving.SelectMany(NodeKeys), StringComparer.OrdinalIgnoreCase);
                    SetupExistingRelation part = new SetupExistingRelation { Name = name, IsAxis = existing.IsAxis, Function = existing.Function };
                    part.Nodes.AddRange(existing.Nodes.Where(n => moved.Contains(SetupPlanner.CommandKey(n.Game, n.Action, SetupPlanner.NodeKey(n)))));
                    existing.Nodes.RemoveAll(n => moved.Contains(SetupPlanner.CommandKey(n.Game, n.Action, SetupPlanner.NodeKey(n))));
                    request.Existing.Add(part);
                    made.Add(name);
                    proposal.Changes.Add(new Change
                    {
                        Id = "split:" + name,
                        Kind = "split",
                        Title = name,
                        Scope = existing.Name,
                        Old = rule.Class,
                        New = name,
                        OldText = existing.Name,
                        Reason = "the " + SetupClasses.Title(rule.Class).ToLowerInvariant() + " commands of \"" + existing.Name + "\" (" + moving.Count.ToString(CultureInfo.InvariantCulture) + ") get a relation of their own",
                        Preference = rule.Id,
                        NewRelation = true
                    });
                }
            }
            return made;
        }

        private static IEnumerable<string> NodeKeys(RelationItem node)
        {
            string game = string.IsNullOrWhiteSpace(node.Game) || EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game;
            if (game != "DCS")
                return new[] { SetupPlanner.CommandKey(game, node.ID, game) };
            return (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => SetupPlanner.CommandKey("DCS", node.ID, p.Key));
        }

        // The commands of a relation that serve only aircraft of one class (whole commands).
        internal static List<RelationItem> NodesOfClass(Relation relation, string cls, Dictionary<string, SetupAircraft> aircraft, out List<string> mixed)
        {
            List<RelationItem> moving = new List<RelationItem>();
            mixed = new List<string>();
            foreach (RelationItem node in relation.NODES.Where(n => n != null))
            {
                string game = string.IsNullOrWhiteSpace(node.Game) || EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game;
                List<string> keys = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key).ToList() : new List<string> { game };
                List<string> classes = keys.Select(k =>
                {
                    SetupAircraft plane;
                    return aircraft.TryGetValue(k, out plane) && !plane.SharedLayer ? ClassOf(plane, aircraft) : string.Empty;
                }).Where(c => c.Length > 0).Distinct().ToList();
                if (classes.Count == 1 && classes[0] == cls)
                    moving.Add(node);
                else if (classes.Contains(cls))
                    mixed.Add(node.ID);
            }
            return moving;
        }

        // Moves the commands of one class into the new relation (Apply).
        private static bool ApplySplit(Change change, List<string> skipped)
        {
            Relation source;
            if (!InternalDataManagement.AllRelations.TryGetValue(change.Scope, out source) || source == null)
            {
                skipped.Add(change.Title + ": \"" + change.Scope + "\" is gone");
                return false;
            }
            if (InternalDataManagement.AllRelations.ContainsKey(change.Title))
            {
                skipped.Add(change.Title + ": a relation of that name exists");
                return false;
            }
            Dictionary<string, SetupAircraft> aircraft;
            Dictionary<string, SetupFunction> functions;
            List<SetupCommand> commands;
            SetupCatalog.Load(EngineSetupWizard.FunctionsFolder(), new List<string>(), out functions, out aircraft, out commands);
            List<string> mixed;
            List<RelationItem> moving = NodesOfClass(source, change.Old, aircraft, out mixed);
            if (moving.Count == 0 || moving.Count == source.NODES.Count(n => n != null))
            {
                skipped.Add(change.Title + ": nothing to move");
                return false;
            }
            Relation part = new Relation { NAME = change.Title, ISAXIS = source.ISAXIS, Groups = (source.Groups ?? new List<string>()).ToList(), NODES = new List<RelationItem>() };
            foreach (RelationItem node in moving)
            {
                source.NODES.Remove(node);
                part.NODES.Add(node);
            }
            InternalDataManagement.AllRelations[part.NAME] = part;
            return true;
        }

        // The games the profile's relations use.
        private static List<string> ProfileGames()
        {
            HashSet<string> games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null))
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null))
                    games.Add(string.IsNullOrWhiteSpace(node.Game) || EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game);
            }
            return games.OrderBy(g => g == "DCS" ? 0 : 1).ThenBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static readonly Dictionary<string, string[]> ShortNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", new[] { "dcs", "dcs world" } },
            { "StarCitizen", new[] { "star citizen", "sc" } },
            { "EliteDangerous", new[] { "elite", "elite dangerous" } },
            { "X4", new[] { "x4" } },
            { "IL2Game", new[] { "il-2", "il2", "great battles" } },
            { "IL2Korea", new[] { "il-2 korea", "korea" } },
            { "MSFS2024", new[] { "msfs", "msfs 2024", "flight simulator" } },
            { "MSFS2020", new[] { "msfs 2020" } },
            { "XPlane12", new[] { "x-plane", "xplane", "x-plane 12", "xp12" } },
            { "XPlane11", new[] { "x-plane 11", "xp11" } },
            { "WarThunder", new[] { "war thunder", "wt" } },
            { "FalconBMS", new[] { "bms", "falcon", "falcon bms" } }
        };

        private static Dictionary<string, string[]> GameWords(IEnumerable<string> games)
        {
            Dictionary<string, string[]> words = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string game in games.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<string> names = new List<string> { game.ToLowerInvariant(), EngineGameWords.Label(game).ToLowerInvariant() };
                string[] extra;
                if (ShortNames.TryGetValue(game, out extra))
                    names.AddRange(extra);
                words[game] = names.Where(n => n.Length >= 2).Distinct().ToArray();
            }
            return words;
        }

        // The local AI model rewrites what the rules did not understand into the phrases they know (never into steps of
        // its own); only phrases the rules then read count.
        private static void AskModel(LayoutUnderstanding understood, Dictionary<string, string[]> gameWords, List<string> planes, string mode, Proposal proposal)
        {
            EngineLlm.ModelInfo model;
            string problem;
            if (!EngineLlm.TryGetUsableModel(out model, out problem))
            {
                if (mode == "on")
                    proposal.Warnings.Add("The local AI model was not asked: " + problem);
                return;
            }
            List<EngineLlm.Message> messages = new List<EngineLlm.Message>
            {
                new EngineLlm.Message { Role = "system", Content = PhrasePrompt },
                new EngineLlm.Message { Role = "user", Content = "Ich fliege meistens Hubschrauber, die Kurven sollen weicher sein und keine Modifier." },
                new EngineLlm.Message { Role = "assistant", Content = "{\"phrases\":[\"curves softer for helis\",\"no modifiers\"]}" },
                new EngineLlm.Message { Role = "user", Content = "In Star Citizen I want to steer the yaw with my left stick by twisting it, not with my feet." },
                new EngineLlm.Message { Role = "assistant", Content = "{\"phrases\":[\"for space games use the left stick and its twist axis instead of pedals\"]}" },
                new EngineLlm.Message { Role = "user", Content = string.Join("\n", understood.NotUnderstood) }
            };
            EngineProgress.Step(0, 2, "Starting the local AI model", EngineRelationPlan.ModelName(model));
            EngineLlm.Session session = EngineLlm.Session.Start(model, EngineRelationPlan.PlanContextSize(model));
            try
            {
                if (session.Error.Length > 0)
                {
                    proposal.Warnings.Add("The local AI model did not start: " + session.Error);
                    return;
                }
                EngineProgress.Step(1, 2, "The local AI model reads your request", EngineRelationPlan.ModelName(model));
                EngineLlm.CallResult call = session.Ask(messages, null, 400, false);
                if (!string.IsNullOrEmpty(call.Error))
                {
                    proposal.Warnings.Add("The local AI model did not answer: " + call.Error);
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
                LayoutUnderstanding rewritten = LayoutPhrases.Read(string.Join("\n", phrases), gameWords, planes);
                foreach (LayoutPreference p in rewritten.Preferences)
                {
                    p.Source = "ai";
                    p.Text = string.Join(" ", understood.NotUnderstood);
                    understood.Preferences.Add(p);
                }
                if (rewritten.Preferences.Count > 0)
                {
                    understood.NotUnderstood.Clear();
                    understood.NotUnderstood.AddRange(rewritten.NotUnderstood);
                    proposal.Method = "JoyPro's rules and the local AI model (" + EngineRelationPlan.ModelName(model) + ")";
                }
            }
            finally
            {
                session.Dispose();
            }
        }

        private const string PhrasePrompt =
            "You turn a pilot's wishes for the layout of joystick, throttle and pedals into short English phrases that JoyPro's rules understand. " +
            "Answer only JSON: {\"phrases\":[...]} and nothing else. Phrases JoyPro knows: " +
            "\"throttle and left-hand stick\"; \"use the left stick instead of the throttle\"; \"no pedals\"; \"only stick and throttle\"; " +
            "\"for space games use the left stick\"; \"for space games use the left stick and its twist axis instead of pedals\"; " +
            "\"<function> on the <control> of the <device>\" with function = trim, countermeasures, TMS, DMS, gear, flaps, airbrake, weapon release, guns, radar, zoom, lights, yaw, strafe, wheel brakes, slew, comms, kneeboard, autopilot, master arm, afterburner, shields, boost, " +
            "control = hat, mini-stick, twist, slider, pinky, panel, base, switch, device = main stick, throttle, left-hand stick, collective; \"no <function> on the <control>\"; " +
            "\"curves softer for helis\", \"curves harder for jets\", \"linear pitch in DCS\", \"bigger dead zone\"; " +
            "\"softer ffb for helis\", \"less ffb shake\", \"stronger ffb\", \"no ffb in IL-2\"; \"vjoy for Star Citizen\", \"native for DCS\"; " +
            "\"no modifiers\", \"one modifier\", \"two modifiers\", \"use a mode switch\", \"no mode switch\"; \"like the real aircraft\", \"same button everywhere\"; " +
            "\"keep my layout\"; \"the F-16 first\". Classes: helis, jets, space games; games by name. Leave out what fits none of them.";

        // ---- the changes ----

        private static void BindChanges(SetupPlan plan, Proposal proposal)
        {
            foreach (SetupPlanRelation relation in plan.Relations.Where(r => !r.Kept && !r.Removed && r.Device.Length > 0 && r.Input.Length > 0).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                Bind bind;
                InternalDataManagement.AllBinds.TryGetValue(relation.Name, out bind);
                string old = bind == null || string.IsNullOrWhiteSpace(bind.Joystick) ? string.Empty : BindText(bind.Joystick, relation.IsAxis ? bind.JAxis : bind.JButton, ModifierName(bind));
                string now = BindText(relation.Device, relation.Input, relation.Modifier);
                if (string.Equals(old, now, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool fresh = !InternalDataManagement.AllRelations.ContainsKey(relation.Name);
                proposal.Changes.Add(new Change
                {
                    Id = "bind:" + relation.Name,
                    Kind = "bind",
                    Title = relation.Name,
                    Scope = relation.Modifier,
                    Old = old,
                    New = now + (relation.Control.Length > 0 || relation.Label.Length > 0 ? " (" + JoyPro.DeviceReference.DeviceVocabulary.Describe(relation.Control, relation.Label) + ")" : string.Empty),
                    Device = relation.Device,
                    Input = relation.Input,
                    Control = relation.Control,
                    Label = relation.Label,
                    Reason = relation.Reason,
                    Preference = PreferenceOf(relation.Reason, proposal.Preferences),
                    NewRelation = fresh
                });
            }
        }

        private static string PreferenceOf(string reason, List<LayoutPreference> preferences)
        {
            return reason.IndexOf("your preference", StringComparison.Ordinal) >= 0 ? "function-on" : string.Empty;
        }

        private static string ModifierName(Bind bind)
        {
            Modifier modifier = bind.AllReformers != null && bind.AllReformers.Count > 0 ? Modifier.ReformerToMod(bind.AllReformers[0]) : null;
            return modifier == null ? string.Empty : modifier.name ?? string.Empty;
        }

        // An axis tuning in plain words: "dead zone 2 %, curve +0.15" ("linear" without both).
        private static string TuningText(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
                return string.Empty;
            AxisTuning t = AxisTuning.Parse(format);
            List<string> parts = new List<string>();
            if (t.Deadzone > 0)
                parts.Add("dead zone " + Math.Round(t.Deadzone * 100, 1).ToString(CultureInfo.InvariantCulture) + " %");
            double curve = t.Curvature != null && t.Curvature.Count > 0 ? t.Curvature[0] : 0;
            if (Math.Abs(curve) > 0.0001)
                parts.Add("curve " + (curve > 0 ? "+" : string.Empty) + curve.ToString("0.###", CultureInfo.InvariantCulture));
            if (t.SaturationY > 0 && t.SaturationY < 1)
                parts.Add("travel " + Math.Round(t.SaturationY * 100).ToString(CultureInfo.InvariantCulture) + " %");
            if (t.Inverted)
                parts.Add("inverted");
            return parts.Count == 0 ? "linear" : string.Join(", ", parts);
        }

        // Force feedback values in plain words: "forces 70 %, shake 50 %".
        private static string FfbText(Dictionary<string, double> values)
        {
            if (values == null || values.Count == 0)
                return "default";
            return string.Join(", ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v =>
            {
                string key = v.Key.ToLowerInvariant();
                string word = key.Contains("shake") || key.Contains("vibr") ? "shake" : key.Contains("gain") || key.Contains("force") || key.Contains("strength") ? "forces" : key.Replace('_', ' ');
                return word + " " + Math.Round(v.Value * 100).ToString(CultureInfo.InvariantCulture) + " %";
            }));
        }

        private static string VJoyText(string mode)
        {
            switch ((mode ?? string.Empty).ToLowerInvariant())
            {
                case "": return string.Empty;
                case "native": return "your devices directly";
                case "xbox": return "a virtual Xbox pad";
                case "virtual": return "the virtual joystick (vJoy)";
                default: return mode;
            }
        }

        // A tuning / force feedback scope in plain words: "plane:DCS:UH-1H" -> "UH-1H (DCS)", "game:IL2Game" -> the game.
        private static string ScopeText(string scope)
        {
            string s = scope ?? string.Empty;
            string[] parts = s.Split(':');
            if (parts.Length >= 3 && parts[0].Equals("plane", StringComparison.OrdinalIgnoreCase))
                return string.Join(":", parts.Skip(2)) + " (" + EngineGameWords.Label(parts[1]) + ")";
            if (parts.Length == 2)
                return EngineGameWords.Label(parts[1]);
            return s;
        }

        private static string BindText(string device, string input, string modifier)
        {
            string name = Regex.Replace(device ?? string.Empty, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty).Trim();
            return name + " " + JoyPro.DeviceReference.DeviceVocabulary.InputName(input) + (string.IsNullOrEmpty(modifier) ? string.Empty : " + " + modifier);
        }

        // Axis curves per aircraft class or game: DCS aircraft get their own scope, the other games their game scope.
        private static void TuningChanges(List<LayoutTuningIntent> intents, SetupPlan plan, Dictionary<string, string> functionOf, Dictionary<string, SetupAircraft> aircraft, Proposal proposal)
        {
            if (intents.Count == 0)
                return;
            Dictionary<string, SetupPlanRelation> planned = plan.Relations.Where(r => !r.Removed).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AxisTuning> pending = new Dictionary<string, AxisTuning>(StringComparer.OrdinalIgnoreCase);
            foreach (LayoutTuningIntent intent in intents)
            {
                Regex axes = new Regex(intent.Axes, RegexOptions.IgnoreCase);
                int touched = 0;
                foreach (KeyValuePair<string, string> pair in functionOf.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!axes.IsMatch(pair.Value))
                        continue;
                    Relation relation;
                    bool exists = InternalDataManagement.AllRelations.TryGetValue(pair.Key, out relation) && relation != null;
                    SetupPlanRelation plannedRelation;
                    planned.TryGetValue(pair.Key, out plannedRelation);
                    bool isAxis = exists ? relation.ISAXIS : plannedRelation != null && plannedRelation.IsAxis;
                    Bind bind;
                    InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind);
                    bool bound = bind != null && !string.IsNullOrWhiteSpace(bind.Joystick) || plannedRelation != null && plannedRelation.Device.Length > 0;
                    if (!isAxis || !bound)
                        continue;
                    foreach (string scope in Scopes(pair.Key, exists ? relation : null, plannedRelation, intent, aircraft))
                    {
                        // Relative to the relation's base curve (for every aircraft: to a straight line), so a saved
                        // preference used again gives the same curve, never one more step.
                        string key = pair.Key + "|" + scope;
                        AxisTuning reference = scope == BindTuning.BaseScope || bind == null ? new AxisTuning() : BindTuning.Base(bind);
                        AxisTuning current = (bind == null ? null : BindTuning.Get(bind, scope)) ?? (bind == null ? new AxisTuning() : BindTuning.Base(bind));
                        AxisTuning start;
                        if (!pending.TryGetValue(key, out start))
                            start = current;
                        AxisTuning next = start.Clone();
                        if (intent.Linear)
                            next.Curvature = new List<double> { 0.0 };
                        else if (Math.Abs(intent.CurveDelta) > 1e-9)
                            next.Curvature = new List<double> { Math.Round(Math.Max(-0.5, Math.Min(1.0, (reference.Curvature.Count > 0 ? reference.Curvature[0] : 0) + intent.CurveDelta)), 3) };
                        if (Math.Abs(intent.DeadzoneDelta) > 1e-9)
                            next.Deadzone = Math.Round(Math.Max(0, Math.Min(0.3, reference.Deadzone + intent.DeadzoneDelta)), 3);
                        string before = Describe(current), after = Describe(next);
                        pending[key] = next;
                        proposal.Changes.RemoveAll(c => c.Id == "tuning:" + key);
                        if (before == after)
                            continue;
                        proposal.Changes.Add(new Change
                        {
                            Id = "tuning:" + key,
                            Kind = "tuning",
                            Title = pair.Key,
                            Scope = scope,
                            Old = before,
                            New = next.Format(),
                            OldText = TuningText(before),
                            NewText = TuningText(next.Format()),
                            ScopeText = ScopeText(scope),
                            Reason = after,
                            Preference = intent.Preference
                        });
                        touched++;
                    }
                }
                if (touched == 0)
                    proposal.Notes.Add("No bound axis relation of that kind for: " + intent.Preference);
            }
        }

        private static string Describe(AxisTuning tuning)
        {
            return "curvature " + (tuning.Curvature.Count > 0 ? tuning.Curvature[0] : 0).ToString("0.###", CultureInfo.InvariantCulture) +
                   ", dead zone " + tuning.Deadzone.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> Scopes(string name, Relation relation, SetupPlanRelation planned, LayoutTuningIntent intent, Dictionary<string, SetupAircraft> aircraft)
        {
            List<KeyValuePair<string, string>> planes = new List<KeyValuePair<string, string>>();   // game, plane
            if (relation != null)
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null))
                {
                    string game = string.IsNullOrWhiteSpace(node.Game) || EngineAutoGroup.IsDcs(node.Game) ? "DCS" : node.Game;
                    if (game == "DCS")
                        planes.AddRange((node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => new KeyValuePair<string, string>("DCS", p.Key)));
                    else
                        planes.Add(new KeyValuePair<string, string>(game, game));
                }
            }
            else if (planned != null)
            {
                planes.AddRange(planned.Nodes.Select(n => new KeyValuePair<string, string>(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game, SetupPlanner.NodeKey(n))));
            }
            HashSet<string> scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> plane in planes.Distinct())
            {
                if (intent.Game.Length > 0 && !string.Equals(intent.Game, plane.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                SetupAircraft known;
                string cls = aircraft.TryGetValue(plane.Value, out known) ? ClassOf(known, aircraft) : string.Empty;
                if (known != null && known.SharedLayer)
                    continue;
                if (intent.Class.Length > 0 && cls != intent.Class)
                    continue;
                if (intent.Class.Length == 0 && intent.Game.Length == 0)
                {
                    scopes.Add(BindTuning.BaseScope);
                    continue;
                }
                scopes.Add(plane.Key == "DCS" ? BindTuning.PlaneScope("DCS", plane.Value) : intent.Game.Length > 0 && intent.Class.Length == 0 ? BindTuning.GameScope(plane.Key) : BindTuning.GameScope(plane.Key));
            }
            return scopes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        }

        private static string ClassOf(SetupAircraft plane, Dictionary<string, SetupAircraft> aircraft)
        {
            SetupAircraft basePlane;
            if (plane.VariantOf.Length > 0 && aircraft.TryGetValue(plane.VariantOf, out basePlane))
                return basePlane.Class;
            return plane.Class;
        }

        // Force feedback: the devices with force feedback of the setup, per DCS aircraft of the class (DCS keeps it per
        // aircraft) or game-wide for the other games with a model (IL-2: force / shaking).
        private static void FfbChanges(List<LayoutFfbIntent> intents, List<SetupDevice> devices, SetupPlan plan, Dictionary<string, SetupAircraft> aircraft, Proposal proposal)
        {
            if (intents.Count == 0)
                return;
            List<SetupDevice> ffb = devices.Where(d => d.ForceFeedback && SetupRoles.Binds(d.Role)).ToList();
            if (ffb.Count == 0)
            {
                proposal.Notes.Add("No force feedback device in your setup: the force feedback wishes wait until one is connected.");
                return;
            }
            Dictionary<string, string> store = InternalDataManagement.FfbOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> profilePlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null))
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null && (string.IsNullOrWhiteSpace(n.Game) || EngineAutoGroup.IsDcs(n.Game))))
                    profilePlanes.UnionWith((node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key));
            }
            Dictionary<string, Dictionary<string, double>> pending = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (LayoutFfbIntent intent in intents)
            {
                List<Tuple<string, string, string>> targets = new List<Tuple<string, string, string>>();   // game, plane, scope
                if (intent.Game.Length == 0 || EngineAutoGroup.IsDcs(intent.Game))
                {
                    foreach (string plane in profilePlanes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        SetupAircraft known;
                        if (!aircraft.TryGetValue(plane, out known) || known.SharedLayer || (intent.Class.Length > 0 && ClassOf(known, aircraft) != intent.Class))
                            continue;
                        targets.Add(Tuple.Create("DCS", plane, BindTuning.PlaneScope("DCS", plane)));
                    }
                }
                foreach (string game in new[] { "IL2Game", "IL2Korea" }.Where(g => proposal.Games.Contains(g, StringComparer.OrdinalIgnoreCase) && (intent.Game.Length == 0 || string.Equals(intent.Game, g, StringComparison.OrdinalIgnoreCase)) && (intent.Class.Length == 0 || intent.Class == SetupClasses.Fixed)))
                    targets.Add(Tuple.Create(game, string.Empty, BindTuning.GameScope(game)));
                foreach (Tuple<string, string, string> target in targets)
                {
                    FfbModel model = FfbSettings.ForGame(target.Item1);
                    if (model == null)
                        continue;
                    foreach (SetupDevice device in target.Item1 == "DCS" ? ffb : new List<SetupDevice> { null })
                    {
                        string deviceName = device == null ? FfbSettings.AnyDevice : device.Device;
                        string key = FfbSettings.Key(deviceName, target.Item3);
                        Dictionary<string, double> current;
                        if (!pending.TryGetValue(key, out current))
                        {
                            string text;
                            current = store.TryGetValue(key, out text) ? FfbSettings.Parse(text) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        }
                        Dictionary<string, double> next = new Dictionary<string, double>(current, StringComparer.OrdinalIgnoreCase);
                        foreach (string param in FfbParameters(target.Item1, intent.Kind))
                        {
                            FfbParameter definition = FfbSettings.Parameter(target.Item1, param);
                            if (definition == null)
                                continue;
                            // Relative to the game's default (a saved preference used again gives the same value).
                            double value = definition.Default;
                            if (intent.Kind == "off")
                                next[param] = param == "ignore" ? 1 : 0;
                            else
                                next[param] = Math.Round(Math.Max(definition.Min, Math.Min(definition.Max, value * intent.Factor)), 3);
                        }
                        pending[key] = next;
                        proposal.Changes.RemoveAll(c => c.Id == "ffb:" + key);
                        if (FfbSettings.Format(current) == FfbSettings.Format(next))
                            continue;
                        proposal.Changes.Add(new Change
                        {
                            Id = "ffb:" + key,
                            Kind = "ffb",
                            Title = (device == null ? EngineGameWords.Label(target.Item1) : Regex.Replace(device.Device, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty)),
                            Scope = target.Item3,
                            Old = FfbSettings.Format(current),
                            New = FfbSettings.Format(next),
                            OldText = FfbText(current),
                            NewText = FfbText(next),
                            ScopeText = ScopeText(target.Item3),
                            Reason = target.Item2.Length > 0 ? target.Item2 : EngineGameWords.Label(target.Item1),
                            Preference = intent.Preference
                        });
                    }
                }
            }
        }

        private static IEnumerable<string> FfbParameters(string game, string kind)
        {
            bool dcs = string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
            switch (kind)
            {
                case "shake":
                    return dcs ? new[] { "shake" } : new[] { "amplitude" };
                case "off":
                    return dcs ? new[] { "ignore" } : new[] { "enabled" };
                default:
                    return dcs ? new[] { "trimmer" } : new[] { "force" };
            }
        }

        // The virtual joystick per game: the games named, or the space games / every game of the class.
        private static void VJoyChanges(List<LayoutVJoyIntent> intents, Dictionary<string, SetupAircraft> aircraft, Proposal proposal)
        {
            if (intents.Count == 0)
                return;
            VJoySettingsView settings = VJoySettingsView.Load();
            foreach (LayoutVJoyIntent intent in intents)
            {
                List<string> games = new List<string>();
                if (intent.Game.Length > 0)
                    games.Add(intent.Game);
                else if (intent.Class == SetupClasses.Space)
                    games.AddRange(proposal.Games.Where(g => EngineGameWords.SpaceGames.Contains(g)));
                else if (intent.Class.Length == 0)
                    games.AddRange(proposal.Games);
                else
                    proposal.Notes.Add("The virtual joystick is set per game, not per aircraft class: say the game (\"vJoy for Star Citizen\").");
                foreach (string game in games.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string old = settings.Mode(game);
                    string mode = intent.Mode == "virtual" ? "virtual" : intent.Mode;
                    proposal.Changes.RemoveAll(c => c.Id == "vjoy:" + game);
                    if (old == mode)
                        continue;
                    proposal.Changes.Add(new Change
                    {
                        Id = "vjoy:" + game,
                        Kind = "vjoy",
                        Title = EngineGameWords.Label(game),
                        Scope = game,
                        Old = old,
                        New = mode,
                        OldText = VJoyText(old),
                        NewText = VJoyText(mode),
                        ScopeText = EngineGameWords.Label(game),
                        Reason = mode == "virtual" ? "vJoy, the physical devices hidden from the game" : mode == "xbox" ? "a virtual Xbox pad" : "the physical devices",
                        Preference = intent.Preference
                    });
                }
            }
        }

        private sealed class VJoySettingsView
        {
            private Dictionary<string, string> games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public static VJoySettingsView Load()
            {
                VJoySettingsView view = new VJoySettingsView();
                try
                {
                    JoyPro.Engine.VJoy.VJoySettings settings = EngineVJoy.LoadSettings();
                    if (settings != null && settings.Games != null)
                        view.games = new Dictionary<string, string>(settings.Games, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                }
                return view;
            }

            public string Mode(string game)
            {
                string mode;
                return games.TryGetValue(game, out mode) && !string.IsNullOrEmpty(mode) ? mode : "native";
            }
        }

        // ---- apply ----

        internal sealed class ApplyResult
        {
            public int Binds;
            public int Relations;
            public int Splits;
            public int Tuning;
            public int Ffb;
            public int VJoy;
            public List<string> Skipped = new List<string>();
            public List<string> Problems = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        public static ApplyResult Apply(Proposal proposal, HashSet<string> skip, bool dryRun)
        {
            ApplyResult result = new ApplyResult();
            // A relation that is not split gets no bind of its own either.
            foreach (Change change in proposal.Changes.Where(c => c.Kind == "split" && skip.Contains(c.Id)))
                skip.Add("bind:" + change.Title);
            List<Change> chosen = proposal.Changes.Where(c => !skip.Contains(c.Id)).ToList();
            HashSet<string> bindNames = new HashSet<string>(chosen.Where(c => c.Kind == "bind").Select(c => c.Title), StringComparer.OrdinalIgnoreCase);

            // Splits (the commands of one aircraft class into a relation of their own) before the binds.
            foreach (Change change in chosen.Where(c => c.Kind == "split"))
            {
                if (dryRun || ApplySplit(change, result.Skipped))
                    result.Splits++;
                else
                    bindNames.Remove(change.Title);
            }

            // Binds: the plan with the changes that were not chosen taken out.
            if (bindNames.Count > 0 && File.Exists(proposal.PlanFile))
            {
                SetupPlan plan = SetupPlanJson.Read(File.ReadAllText(proposal.PlanFile, Encoding.UTF8));
                foreach (SetupPlanRelation relation in plan.Relations.Where(r => !r.Kept))
                {
                    if (!bindNames.Contains(relation.Name))
                        relation.Removed = true;
                }
                EngineSetupWizard.ApplyResult applied = EngineSetupWizard.Apply(plan, dryRun, false);
                result.Problems.AddRange(applied.Problems);
                if (applied.Problems.Count > 0)
                    return result;
                result.Binds = applied.Bound;
                result.Relations = applied.Created;
                result.Skipped.AddRange(applied.Skipped.Take(30));
            }
            else if (bindNames.Count > 0)
            {
                result.Problems.Add("The plan of this preview is gone (" + proposal.PlanFile + "); make the preview again.");
                return result;
            }

            // Axis tuning.
            foreach (Change change in chosen.Where(c => c.Kind == "tuning"))
            {
                Bind bind;
                if (!InternalDataManagement.AllBinds.TryGetValue(change.Title, out bind) || bind == null)
                {
                    if (!dryRun)
                        result.Skipped.Add(change.Title + ": no bind, the curve was not set");
                    continue;
                }
                if (!dryRun)
                    BindTuning.Set(bind, change.Scope, AxisTuning.Parse(change.New));
                result.Tuning++;
            }

            // Force feedback.
            foreach (Change change in chosen.Where(c => c.Kind == "ffb"))
            {
                string key = change.Id.Substring("ffb:".Length);
                if (!dryRun)
                {
                    if (InternalDataManagement.FfbOverrides == null)
                        InternalDataManagement.FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    InternalDataManagement.FfbOverrides[key] = change.New;
                }
                result.Ffb++;
            }

            // The virtual joystick.
            foreach (Change change in chosen.Where(c => c.Kind == "vjoy"))
            {
                if (!dryRun)
                {
                    string error = EngineVJoy.SetMode(change.Scope, change.New, result.Warnings);
                    if (error.Length > 0)
                    {
                        result.Skipped.Add(change.Title + ": " + error);
                        continue;
                    }
                }
                result.VJoy++;
            }

            if (!dryRun)
                SavePreferences(proposal.Preferences);
            return result;
        }

        // ---- text ----

        public static string ToText(Proposal proposal)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Lay out with AI");
            sb.AppendLine("Request: " + proposal.Text);
            sb.AppendLine("Understood by: " + proposal.Method);
            sb.AppendLine("Games: " + string.Join(", ", proposal.Games));
            sb.AppendLine("Preferences:");
            foreach (LayoutPreference p in proposal.Preferences)
                sb.AppendLine("  - " + (proposal.Fresh.Contains(p.Key) ? "[new] " : "[saved] ") + p.Describe() + (p.Source == "ai" ? " (local AI)" : string.Empty) + " | " + p.Key);
            if (proposal.NotUnderstood.Count > 0)
            {
                sb.AppendLine("Not understood:");
                foreach (string clause in proposal.NotUnderstood)
                    sb.AppendLine("  - " + clause);
            }
            sb.AppendLine("Changes: " + proposal.Binds.ToString(CultureInfo.InvariantCulture) + " binds, " + proposal.Changes.Count(c => c.Kind == "split").ToString(CultureInfo.InvariantCulture) + " splits, " + proposal.Tuning.ToString(CultureInfo.InvariantCulture) + " axis curves, " +
                          proposal.Ffb.ToString(CultureInfo.InvariantCulture) + " force feedback, " + proposal.VJoy.ToString(CultureInfo.InvariantCulture) + " virtual joystick");
            foreach (Change change in proposal.Changes)
                sb.AppendLine("  - " + change.Kind + " | " + change.Title + (change.Scope.Length > 0 && change.Kind != "bind" ? " [" + change.Scope + "]" : string.Empty) + " | " +
                              (change.Old.Length > 0 ? change.Old : "(none)") + " => " + change.New + (change.Reason.Length > 0 ? " | " + change.Reason : string.Empty));
            if (proposal.Notes.Count > 0)
            {
                sb.AppendLine("Notes:");
                foreach (string note in proposal.Notes.Distinct())
                    sb.AppendLine("  - " + note);
            }
            if (proposal.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in proposal.Warnings.Distinct().Take(20))
                    sb.AppendLine("  - " + warning);
            }
            return sb.ToString();
        }
    }

    internal static partial class Program
    {
        private static int RunArrange(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "prefs" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            switch (action)
            {
                case "ask":
                {
                    EngineSetupWizard.DeviceChoices choices;
                    string error = ParseSetupDeviceChoices(args, out choices);
                    if (error != null)
                        return Fail(error);
                    string llm = (PopOptionValue(args, "--llm") ?? "auto").Trim().ToLowerInvariant();
                    if (!EngineNlGroup.LlmModes.Contains(llm))
                        return Fail("Invalid --llm value: " + llm + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
                    List<string> games = SplitList(PopOptionValue(args, "--games"));
                    bool addMissing = PopFlag(args, "--add-missing");
                    bool forget = PopFlag(args, "--forget-saved");
                    string save = PopOptionValue(args, "--proposal") ?? PopOptionValue(args, "--out-proposal");
                    string text = string.Join(" ", args).Trim();
                    if (text.Length == 0 && (forget || EngineLayout.SavedPreferences().Count == 0))
                        return Fail("arrange ask needs your preferences in words, e.g. engine arrange ask \"throttle and left-hand stick, trim on the hat, curves softer for helis\".");
                    string target = string.IsNullOrWhiteSpace(save) ? Path.Combine(Path.GetTempPath(), "JoyPro2", "layout-ai", "proposal.json") : NormalizePath(save);
                    EngineLayout.Proposal proposal = EngineLayout.Ask(text, llm, games, addMissing, forget, choices, target);
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(target, EngineSerialization.ToJson(proposal), new UTF8Encoding(false));
                    EmitPayload(textOutput ? EngineLayout.ToText(proposal) : EngineSerialization.ToJson(proposal), outPath);
                    return 0;
                }
                case "apply":
                {
                    string file = PopOptionValue(args, "--proposal");
                    HashSet<string> skip = new HashSet<string>(PopOptionValues(args, "--skip"), StringComparer.OrdinalIgnoreCase);
                    bool dryRun = PopFlag(args, "--dry-run");
                    if (args.Count > 0)
                        return Fail("Unknown arrange apply argument(s): " + string.Join(" ", args));
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(NormalizePath(file)))
                        return Fail("arrange apply needs --proposal <proposal.json> (from engine arrange ask).");
                    EngineLayout.Proposal proposal;
                    try
                    {
                        proposal = EngineSerialization.FromJson<EngineLayout.Proposal>(File.ReadAllText(NormalizePath(file), Encoding.UTF8));
                    }
                    catch (Exception ex)
                    {
                        return Fail("The preview could not be read: " + ex.Message);
                    }
                    if (proposal == null || proposal.Format != EngineLayout.ProposalFormat)
                        return Fail("That file is no preview of engine arrange ask.");
                    EngineLayout.ApplyResult result = EngineLayout.Apply(proposal, skip, dryRun);
                    if (result.Problems.Count > 0)
                        return Fail("The layout puts functions of the same aircraft on one input:" + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Select(p => "  - " + p)));
                    if (!dryRun)
                    {
                        InternalDataManagement.ResyncBindsToMods();
                        InternalDataManagement.ResyncRelations();
                        InternalDataManagement.RecalcFigures();
                        // One version in the profile history (Undo takes all of it back).
                        EngineProfileHistory.Note = "Lay out with AI: " + result.Binds.ToString(CultureInfo.InvariantCulture) + " binds, " +
                                                    (result.Splits > 0 ? result.Splits.ToString(CultureInfo.InvariantCulture) + " relations split, " : string.Empty) + result.Tuning.ToString(CultureInfo.InvariantCulture) + " axis curves, " +
                                                    result.Ffb.ToString(CultureInfo.InvariantCulture) + " force feedback, " + result.VJoy.ToString(CultureInfo.InvariantCulture) + " virtual joystick";
                    }
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Lay out with AI: Apply");
                    sb.AppendLine("Dry Run: " + dryRun);
                    sb.AppendLine("Binds Set: " + result.Binds.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Relations Created: " + result.Relations.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Relations Split: " + result.Splits.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Axis Curves Set: " + result.Tuning.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Force Feedback Set: " + result.Ffb.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Virtual Joystick Set: " + result.VJoy.ToString(CultureInfo.InvariantCulture));
                    foreach (string skipped in result.Skipped)
                        sb.AppendLine("Skipped: " + skipped);
                    foreach (string warning in result.Warnings.Distinct())
                        sb.AppendLine("Warning: " + warning);
                    EmitPayload(textOutput ? sb.ToString() : EngineSerialization.ToJson(result), outPath);
                    return 0;
                }
                case "prefs":
                {
                    string sub = args.Count == 0 ? "show" : args[0].ToLowerInvariant();
                    List<LayoutPreference> saved = EngineLayout.SavedPreferences();
                    if (sub == "clear")
                    {
                        EngineLayout.SavePreferences(new List<LayoutPreference>());
                        EngineProfileHistory.Note = "Lay out with AI: preferences cleared";
                        saved = new List<LayoutPreference>();
                    }
                    else if (sub == "remove")
                    {
                        if (args.Count < 2)
                            return Fail("arrange prefs remove <key>");
                        string key = string.Join(" ", args.Skip(1));
                        if (saved.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) == 0)
                            return Fail("No saved preference with the key " + key + ".");
                        EngineLayout.SavePreferences(saved);
                        EngineProfileHistory.Note = "Lay out with AI: preference removed";
                    }
                    else if (sub != "show")
                    {
                        return Fail("arrange prefs [show | clear | remove <key>]");
                    }
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Saved preferences: " + saved.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (LayoutPreference p in saved)
                            sb.AppendLine("  - " + p.Describe() + " | " + p.Key);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(EngineSerialization.ToJson(saved), outPath);
                    }
                    return 0;
                }
                default:
                    return Fail("engine arrange ask \"<your preferences>\" [--llm auto|on|off] [--games g1,g2] [--add-missing] [--forget-saved] [--proposal <file>] | arrange apply --proposal <file> [--skip <id>]... [--dry-run] | arrange prefs [show|clear|remove <key>]");
            }
        }
    }
}
