using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Moves bindings between the loaded JoyPro profile and registry games.
    internal static class GameProfileBridge
    {
        private const string SignatureSeparator = "\u001F";

        // All binds of relations that have an active node for the game. Nodes with a plane map where
        // no plane is active are disabled and not exported.
        public static List<GameInputBinding> CollectBindings(IGameIntegration game, GameIoResult result)
        {
            List<GameInputBinding> output = new List<GameInputBinding>();
            IGameAircraftScope scope = game as IGameAircraftScope;
            string pickedAircraft = scope == null || string.IsNullOrWhiteSpace(scope.ExportAircraft) ? null : scope.ExportAircraft.Trim();
            HashSet<string> warnedRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int limitedExported = 0;
            if (pickedAircraft != null)
            {
                IReadOnlyList<string> known = scope.AircraftKeys();
                if (!known.Contains(pickedAircraft, StringComparer.OrdinalIgnoreCase))
                    result.Warnings.Add("Aircraft '" + pickedAircraft + "' is not in JoyPro's " + game.DisplayName + " aircraft list (" +
                                        (known.Count == 0 ? "refresh the DB" : known.Count.ToString(CultureInfo.InvariantCulture) + " aircraft") + "); only relations limited to exactly this name are added.");
            }
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null)
                    continue;

                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(entry.Key))
                    bind = InternalDataManagement.AllBinds[entry.Key];

                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || !GameRegistry.GameMatches(game, node.Game))
                        continue;
                    List<string> limitedTo = scope == null ? null : LimitedAircraft(node, game.GameId);
                    if (scope == null ? !IsNodeActive(node, game.GameId) : limitedTo != null && limitedTo.Count == 0)
                        continue;

                    string input = bind == null ? string.Empty : (relation.ISAXIS ? bind.JAxis : bind.JButton);
                    if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(input))
                    {
                        result.BindingsSkipped++;
                        continue;
                    }
                    if (limitedTo != null)
                    {
                        if (pickedAircraft == null || !limitedTo.Contains(pickedAircraft, StringComparer.OrdinalIgnoreCase))
                        {
                            result.BindingsSkipped++;
                            if (warnedRelations.Add(relation.NAME ?? entry.Key))
                                result.Warnings.Add("Relation '" + (relation.NAME ?? entry.Key) + "' is limited to " + game.DisplayName + " aircraft " + string.Join(", ", limitedTo) +
                                                    " and was not exported" + (pickedAircraft == null ? "" : " (picked aircraft: " + pickedAircraft + ")") + ". " + scope.PerAircraftNote);
                            continue;
                        }
                        limitedExported++;
                    }

                    // Axis values as the game uses them: its own scope if the relation has one, else the base.
                    AxisTuning tuning = relation.ISAXIS ? BindTuning.Resolve(bind, game.GameId, null).Tuning : BindTuning.Base(bind);
                    output.Add(new GameInputBinding
                    {
                        ActionId = (node.ID ?? string.Empty).Trim(),
                        Device = bind.Joystick.Trim(),
                        Input = input.Trim(),
                        IsAxis = relation.ISAXIS,
                        Reformers = bind.AllReformers == null ? new List<string>() : new List<string>(bind.AllReformers),
                        Inverted = relation.ISAXIS ? tuning.Inverted : bind.Inverted ?? false,
                        Deadzone = tuning.Deadzone,
                        SaturationX = tuning.SaturationX,
                        SaturationY = tuning.SaturationY,
                        Curvature = new List<double>(tuning.Curvature),
                        RelationName = relation.NAME,
                        Tuning = relation.ISAXIS ? tuning : null
                    });
                }
            }

            if (pickedAircraft != null)
                result.Steps.Add("Aircraft picked: " + pickedAircraft + ". Exported the relations for all aircraft and " + limitedExported.ToString(CultureInfo.InvariantCulture) +
                                 " bind(s) limited to it. " + scope.PerAircraftNote);
            return output;
        }

        // Games with an aircraft list: null when the node is for all aircraft (no entry, or the game id active), else
        // the aircraft it is limited to (empty = switched off for every aircraft).
        public static List<string> LimitedAircraft(RelationItem node, string gameId)
        {
            if (node == null || node.AIRCRAFT == null || node.AIRCRAFT.Count == 0)
                return null;
            bool all;
            if (node.AIRCRAFT.TryGetValue(gameId, out all) && all)
                return null;
            // A node with the old id of a game split into versions (MSFS) keeps "all aircraft" under that id.
            string legacy = LegacyGameIds.LegacyOf(gameId);
            if (legacy.Length > 0 && node.AIRCRAFT.TryGetValue(legacy, out all) && all)
                return null;
            return node.AIRCRAFT
                .Where(p => p.Value && !string.IsNullOrWhiteSpace(p.Key) && !string.Equals(p.Key, gameId, StringComparison.OrdinalIgnoreCase) &&
                            (legacy.Length == 0 || !string.Equals(p.Key, legacy, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Key.Trim())
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsNodeActive(RelationItem node, string gameId)
        {
            if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0)
                return true;
            bool value;
            if (node.AIRCRAFT.TryGetValue(gameId, out value))
                return value;
            return node.AIRCRAFT.Values.Any(v => v);
        }

        // Applies imported bindings. A binding that a relation with its node already has is kept as is; it fills
        // a relation with its node but no bind; it joins a relation with the same device/input/modifiers; and
        // only otherwise creates a relation named after the action. An action bound on two devices (or twice on
        // one) therefore gets a relation per bind, as in the DCS import, instead of losing the second bind.
        // Returns the bindings that are now in the profile.
        public static List<GameInputBinding> ApplyImport(IGameIntegration game, GameCatalog catalog, List<GameInputBinding> bindings, bool merge, GameIoResult result)
        {
            List<GameInputBinding> applied = new List<GameInputBinding>();
            Dictionary<string, Relation> relations = merge
                ? new Dictionary<string, Relation>(InternalDataManagement.AllRelations, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Bind> binds = merge
                ? new Dictionary<string, Bind>(InternalDataManagement.AllBinds, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);

            // JoyPro's own data of the binds it exported to this file (relation names, groups, modifiers, tuning).
            Dictionary<string, BindMetadata> stored = GameMetadataStore.Load(result.ConfigPath, result.Warnings);
            Dictionary<string, string> relationBySignature = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in relations)
            {
                Bind existingBind;
                if (entry.Value != null && binds.TryGetValue(entry.Key, out existingBind) && existingBind != null)
                {
                    string input = entry.Value.ISAXIS ? existingBind.JAxis : existingBind.JButton;
                    string signature = BuildSignature(existingBind.Joystick, entry.Value.ISAXIS, input, existingBind.AllReformers);
                    if (!relationBySignature.ContainsKey(signature))
                        relationBySignature[signature] = entry.Key;
                }
            }

            foreach (GameInputBinding binding in bindings)
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.ActionId))
                {
                    result.BindingsSkipped++;
                    continue;
                }

                GameAction action = catalog.Find(binding.ActionId);
                bool isAxis = action != null ? action.IsAxis : binding.IsAxis;
                string bindingSignature = BuildSignature(binding.Device, isAxis, binding.Input, binding.Reformers);
                BindMetadata meta;
                stored.TryGetValue(GameMetadataStore.Key(binding.ActionId, binding.Device, isAxis, binding.Input, binding.Reformers), out meta);

                List<string> relationsWithNode = relations
                    .Where(r => r.Value != null && r.Value.NODES != null &&
                                r.Value.NODES.Any(n => n != null && GameRegistry.GameMatches(game, n.Game) &&
                                                       string.Equals((n.ID ?? string.Empty).Trim(), binding.ActionId, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => r.Key)
                    .ToList();
                string unbound = null;
                bool alreadyThere = false;
                foreach (string name in relationsWithNode)
                {
                    Bind existing;
                    binds.TryGetValue(name, out existing);
                    string existingInput = existing == null ? string.Empty : (relations[name].ISAXIS ? existing.JAxis : existing.JButton);
                    if (existing == null || string.IsNullOrWhiteSpace(existing.Joystick) || string.IsNullOrWhiteSpace(existingInput))
                    {
                        if (unbound == null && relations[name].ISAXIS == isAxis)
                            unbound = name;
                    }
                    else if (string.Equals(BuildSignature(existing.Joystick, relations[name].ISAXIS, existingInput, existing.AllReformers), bindingSignature, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyThere = true;
                        break;
                    }
                }
                if (alreadyThere)
                {
                    applied.Add(binding);
                    continue;
                }
                if (unbound != null)
                {
                    Bind filled = CreateBind(relations[unbound], binding, action, game);
                    binds[unbound] = filled;
                    relationBySignature[bindingSignature] = unbound;
                    result.NodesAddedToExisting++;
                    applied.Add(binding);
                    continue;
                }

                // Exported by JoyPro: back into the relation it came from (another of its actions may have created it).
                Relation known;
                if (meta != null && relations.TryGetValue(meta.RelationName, out known) && known != null && known.ISAXIS == isAxis &&
                    binds.ContainsKey(meta.RelationName) && binds[meta.RelationName] != null &&
                    string.Equals(BuildSignature(binds[meta.RelationName].Joystick, isAxis, isAxis ? binds[meta.RelationName].JAxis : binds[meta.RelationName].JButton, binds[meta.RelationName].AllReformers),
                        bindingSignature, StringComparison.OrdinalIgnoreCase))
                {
                    known.AddNode(binding.ActionId, game.GameId, isAxis, game.GameId, false);
                    result.NodesAddedToExisting++;
                    applied.Add(binding);
                    continue;
                }

                string relationBySameInput;
                // Same input, same relation; an axis the game inverts for one action and not for the other stays a
                // relation of its own (one relation has one invert: the export would change the other action).
                if (meta == null && relationBySignature.TryGetValue(bindingSignature, out relationBySameInput) &&
                    relations.ContainsKey(relationBySameInput) &&
                    relations[relationBySameInput].ISAXIS == isAxis &&
                    (!isAxis || !binds.ContainsKey(relationBySameInput) || binds[relationBySameInput] == null || binds[relationBySameInput].Inverted == binding.Inverted))
                {
                    relations[relationBySameInput].AddNode(binding.ActionId, game.GameId, isAxis, game.GameId, false);
                    result.NodesAddedToExisting++;
                    applied.Add(binding);
                    continue;
                }

                string title = action != null && !string.IsNullOrWhiteSpace(action.Title) ? action.Title : binding.ActionId;
                string relationName = meta != null ? UniqueRelationName(meta.RelationName, relations) : NewRelationName(title, game, relations);
                Relation relation = new Relation();
                relation.NAME = relationName;
                relation.ISAXIS = isAxis;
                // A relation JoyPro exported keeps its groups, also none; others are marked as generated.
                relation.Groups = meta != null
                    ? new List<string>(meta.Groups ?? new List<string>())
                    : new List<string> { "GENERATED", game.DisplayName };
                relation.NODES = new List<RelationItem>();
                relation.AddNode(binding.ActionId, game.GameId, isAxis, game.GameId, false);

                Bind bind = CreateBind(relation, binding, action, game);
                if (meta != null)
                {
                    List<string> reformers = meta.ReformersMatching(binding.Device, isAxis, binding.Input, binding.Reformers);
                    if (reformers != null)
                        bind.AllReformers = reformers;
                    if (isAxis && meta.HasTuning)
                    {
                        // JoyPro's tuning (base and every scope); values changed in the game since the export become
                        // the game's tuning on top.
                        string fileNow = BindMetadata.FileTuningOf(binding);
                        AxisTuning gameScope = BindTuning.Get(bind, BindTuning.GameScope(game.GameId));
                        meta.ApplyTuning(bind);
                        if (!string.IsNullOrEmpty(meta.FileTuning) && !string.Equals(fileNow, meta.FileTuning, StringComparison.Ordinal))
                            BindTuning.Set(bind, BindTuning.GameScope(game.GameId), gameScope ?? AxisTuning.Parse(fileNow));
                    }
                    if (!string.IsNullOrWhiteSpace(meta.Alias))
                        bind.AliasJoystick = meta.Alias;
                }
                relation.bind = bind;
                relations[relationName] = relation;
                binds[relationName] = bind;
                relationBySignature[bindingSignature] = relationName;
                result.RelationsImported++;
                applied.Add(binding);
            }

            InternalDataManagement.AllRelations = relations;
            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            return applied;
        }

        // The game's axis values (dead zone, saturation, curvature, its own response settings) become the relation's
        // tuning for that game, so they do not change what other games (DCS) get from the bind's base.
        private static Bind CreateBind(Relation relation, GameInputBinding binding, GameAction action, IGameIntegration game)
        {
            Bind bind = new Bind(relation);
            bind.Joystick = binding.Device;
            bind.JoyBackup = binding.Device;
            if (relation.ISAXIS)
            {
                bind.JAxis = binding.Input;
                bind.JButton = string.Empty;
                bind.Inverted = binding.Inverted;
                AxisTuning tuning = binding.Tuning != null ? binding.Tuning.Clone() : new AxisTuning();
                tuning.Inverted = binding.Inverted;
                if (binding.Tuning == null)
                {
                    tuning.Deadzone = binding.Deadzone;
                    tuning.SaturationX = binding.SaturationX;
                    tuning.SaturationY = binding.SaturationY;
                    tuning.Curvature = binding.Curvature == null || binding.Curvature.Count == 0
                        ? new List<double>(Bind.Curvature_Default)
                        : new List<double>(binding.Curvature);
                }
                bool neutral = tuning.Deadzone == 0 && tuning.SaturationX == 1 && tuning.SaturationY == 1 &&
                               tuning.Curvature.All(c => c == 0) && tuning.Curvature.Count == 1 && tuning.Native.Values.All(v => v == 0);
                if (!neutral || (binding.KeepTuning && binding.Tuning != null))
                    BindTuning.Set(bind, BindTuning.GameScope(game.GameId), tuning);
            }
            else
            {
                bind.JButton = binding.Input;
                bind.JAxis = string.Empty;
                bind.Inverted = null;
                bind.Curvature = new List<double>(Bind.Curvature_Default);
            }

            bind.AllReformers = binding.Reformers == null ? new List<string>() : new List<string>(binding.Reformers);
            bind.AdditionalImportInfo = action != null ? action.Title : binding.ActionId;
            string alias;
            if (InternalDataManagement.JoystickAliases != null &&
                InternalDataManagement.JoystickAliases.TryGetValue(binding.Device ?? string.Empty, out alias))
                bind.AliasJoystick = alias ?? string.Empty;
            return bind;
        }

        public static string BuildSignature(string device, bool axis, string input, IEnumerable<string> reformers)
        {
            List<string> normalizedReformers = (reformers ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r =>
                {
                    string[] parts = r.Split('§');
                    // Name and switch flag do not change which physical input is pressed.
                    return parts.Length >= 3 ? (parts[1] + "§" + parts[2]).Trim().ToLowerInvariant() : r.Trim().ToLowerInvariant();
                })
                .Distinct()
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
            return (device ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (axis ? "1" : "0") + SignatureSeparator +
                   (input ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   string.Join(SignatureSeparator, normalizedReformers);
        }

        // The name of a relation an import makes: the action's readable title ("Arm Auto Throttle"); when a relation of
        // that name exists, the game in brackets ("Arm Auto Throttle (Microsoft Flight Simulator 2024)") if that one
        // belongs to another game, else a number ("Arm Auto Throttle (2)").
        public static string NewRelationName(string title, IGameIntegration game, Dictionary<string, Relation> existing)
        {
            return NewRelationName(title, game.GameId, game.DisplayName, existing);
        }

        public static string NewRelationName(string title, string gameId, string gameName, Dictionary<string, Relation> existing)
        {
            string root = (title ?? string.Empty).Trim();
            if (root.Length == 0)
                root = "Relation";
            Relation clash;
            if (existing.TryGetValue(root, out clash) && clash != null &&
                !(clash.NODES ?? new List<RelationItem>()).Any(n => n != null && string.Equals(n.Game, gameId, StringComparison.OrdinalIgnoreCase)))
                root = root + " (" + GameLabel(gameName, gameId) + ")";
            return UniqueRelationName(root, existing);
        }

        // "Elite Dangerous (experimental)" -> "Elite Dangerous".
        private static string GameLabel(string gameName, string gameId)
        {
            string name = (string.IsNullOrWhiteSpace(gameName) ? gameId ?? string.Empty : gameName).Trim();
            const string experimental = " (experimental)";
            return name.EndsWith(experimental, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - experimental.Length).Trim() : name;
        }

        private static string UniqueRelationName(string baseName, Dictionary<string, Relation> existing)
        {
            string root = baseName.Trim();
            string candidate = root;
            int index = 2;
            while (existing.ContainsKey(candidate))
            {
                candidate = root + " (" + index.ToString(CultureInfo.InvariantCulture) + ")";
                index++;
            }
            return candidate;
        }
    }
}
