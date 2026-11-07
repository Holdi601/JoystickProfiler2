using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine
{
    // Experimental (2.6.5, docs/multi-bind.md): a relation can have several binds. The main bind is Relation.bind (and
    // InternalDataManagement.AllBinds[name], as always); the others are Relation.ExtraBinds, each with its own device,
    // input, modifiers and axis tuning. With the setting off (the default, global option --multi-bind off) nothing reads
    // the extra binds: exports, checks and pages behave exactly as before; extra binds a profile already has stay in it
    // and an export names them once ("not exported: the setting is off").
    internal static class MultiBind
    {
        public const string Option = "--multi-bind";

        // Set per run from --multi-bind on|off (the GUI passes on while Settings > Experimental > "Several inputs per
        // relation" is ticked); JOYPRO_MULTI_BIND=1 switches it on for hand-typed commands.
        public static bool Enabled;

        public static bool ParseOption(string raw, out bool enabled)
        {
            enabled = false;
            if (raw == null)
            {
                enabled = string.Equals(Environment.GetEnvironmentVariable("JOYPRO_MULTI_BIND"), "1", StringComparison.Ordinal);
                return true;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    enabled = true;
                    return true;
                case "off":
                case "false":
                case "0":
                    return true;
                default:
                    return false;
            }
        }

        public static List<Bind> Extras(Relation relation)
        {
            return relation == null || relation.ExtraBinds == null ? new List<Bind>() : relation.ExtraBinds.Where(b => b != null).ToList();
        }

        public static bool HasExtras(Relation relation)
        {
            return relation != null && relation.ExtraBinds != null && relation.ExtraBinds.Any(b => b != null);
        }

        public static string InputOf(Relation relation, Bind bind)
        {
            if (bind == null)
                return string.Empty;
            return ((relation != null && relation.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty).Trim();
        }

        public static bool IsBound(Relation relation, Bind bind)
        {
            return bind != null && !string.IsNullOrWhiteSpace(bind.Joystick) && InputOf(relation, bind).Length > 0;
        }

        // The extra binds an export, a check or a page uses: none while the setting is off.
        public static List<Bind> ActiveExtras(Relation relation)
        {
            return Enabled ? Extras(relation) : new List<Bind>();
        }

        // Main bind first, then the extra binds in use (setting on).
        public static List<Bind> ActiveBinds(Relation relation, Bind main)
        {
            List<Bind> binds = new List<Bind>();
            if (main != null)
                binds.Add(main);
            binds.AddRange(ActiveExtras(relation));
            return binds;
        }

        // The profile's binds by relation name, each main bind followed by its relation's extra binds in use (setting on;
        // off: exactly the main binds in their order). For the checks that look at every input (profile validate,
        // relations conflicts): an extra bind is the same relation, so it never conflicts with its own main bind, but a
        // second relation on the same input is found as before.
        public static IEnumerable<KeyValuePair<string, Bind>> WithActiveExtras(Dictionary<string, Bind> binds)
        {
            foreach (KeyValuePair<string, Bind> entry in binds ?? new Dictionary<string, Bind>())
            {
                yield return entry;
                if (!Enabled || entry.Value == null)
                    continue;
                Relation owner = null;
                if (InternalDataManagement.AllRelations != null)
                    InternalDataManagement.AllRelations.TryGetValue(entry.Key ?? string.Empty, out owner);
                if (owner == null)
                    owner = entry.Value.Rl;
                foreach (Bind extra in Extras(owner))
                    yield return new KeyValuePair<string, Bind>(entry.Key, extra);
            }
        }

        // The relation's extra bind on this physical input (BuildSignature form), or null.
        public static Bind FindExtra(Relation relation, string signature)
        {
            return Extras(relation).FirstOrDefault(b => string.Equals(Signature(relation, b), signature, StringComparison.OrdinalIgnoreCase));
        }

        // 1 for the main bind (or a bind that is not the relation's), 2.. for its extra binds (as the pages number them).
        public static int NumberOf(Relation relation, Bind bind)
        {
            if (relation == null || bind == null || relation.ExtraBinds == null)
                return 1;
            int index = relation.ExtraBinds.Where(b => b != null).ToList().IndexOf(bind);
            return index < 0 ? 1 : index + 2;
        }

        // The bind of the relation an exported binding came from (device and input; the main bind when none matches).
        public static Bind BindOf(Relation relation, Bind main, string device, string input)
        {
            if (main == null || (string.Equals((main.Joystick ?? string.Empty).Trim(), (device ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(InputOf(relation, main), (input ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)))
                return main;
            foreach (Bind extra in ActiveExtras(relation))
            {
                if (string.Equals((extra.Joystick ?? string.Empty).Trim(), (device ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(InputOf(relation, extra), (input ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    return extra;
            }
            return main;
        }

        // A new extra bind of the relation (relation.bind stays the main bind).
        public static Bind NewExtra(Relation relation)
        {
            Bind bind = new Bind();
            bind.Rl = relation;
            if (relation != null && !relation.ISAXIS)
                bind.Inverted = null;
            return bind;
        }

        public static void Add(Relation relation, Bind bind)
        {
            if (relation == null || bind == null)
                return;
            if (relation.ExtraBinds == null)
                relation.ExtraBinds = new List<Bind>();
            bind.Rl = relation;
            relation.ExtraBinds.Add(bind);
        }

        // No null entries, every extra bind points at its relation; an empty list is null again (the relation is then
        // written exactly as one that never had extra binds).
        public static void Normalize(Relation relation)
        {
            if (relation == null || relation.ExtraBinds == null)
                return;
            relation.ExtraBinds.RemoveAll(b => b == null);
            foreach (Bind bind in relation.ExtraBinds)
                bind.Rl = relation;
            if (relation.ExtraBinds.Count == 0)
                relation.ExtraBinds = null;
        }

        // The signature of the physical input a bind presses (device, input, modifier keys).
        public static string Signature(Relation relation, Bind bind)
        {
            return Games.GameProfileBridge.BuildSignature(bind == null ? null : bind.Joystick, relation != null && relation.ISAXIS, InputOf(relation, bind),
                bind == null ? null : bind.AllReformers);
        }

        // Relations with extra binds in the profile, and how many extra binds they have.
        public static int CountExtras(out int relations)
        {
            relations = 0;
            int count = 0;
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values)
            {
                int extras = Extras(relation).Count;
                if (extras == 0)
                    continue;
                relations++;
                count += extras;
            }
            return count;
        }

        public enum ExportSupport
        {
            // The game's file keeps several inputs per action: every bind is written.
            All,
            // Several inputs per action, with a limit the game's own export checks and warns about (slots, one axis).
            Limited,
            // One input per action (or one device): the main bind only, a warning per relation.
            MainOnly
        }

        // How a game takes a relation's extra binds (docs/multi-bind.md "Export per game", with the evidence).
        public static ExportSupport SupportOf(string gameId, out string reason)
        {
            string id = (gameId ?? string.Empty).Trim();
            reason = string.Empty;
            if (Starts(id, "StarCitizen"))
            {
                reason = "one joystick input per action";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "HLLVietnam"))
            {
                reason = "one key per action";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "VTOLVRNative") || Starts(id, "Wardogs"))
            {
                reason = "one input per action";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "GRebels"))
            {
                reason = "one key per action and device channel";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "AceCombat8"))
            {
                reason = "one virtual pad control per action";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "Battlefield"))
            {
                reason = "the binds of one joystick only";
                return ExportSupport.MainOnly;
            }
            if (Starts(id, "EliteDangerous") || Starts(id, "MSFS") || Starts(id, "IL21946") || Starts(id, "IL2CoD") || Starts(id, "WarThunder") ||
                Starts(id, "FalconBMS") || Starts(id, "XPlane") || Starts(id, "AceCombat7") || Starts(id, "NuclearOption") || Starts(id, "TinyCombatArena") ||
                Starts(id, "ProjectWingman") || Starts(id, "IL2Game"))
                return ExportSupport.Limited;
            return ExportSupport.All;
        }

        private static bool Starts(string id, string prefix)
        {
            return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // The warning an export gives once while the setting is off and the profile has extra binds for that game.
        public static string OffNote(string gameName, int binds, int relations)
        {
            return binds + " extra input(s) of " + relations + " relation(s) were not exported to " + gameName +
                   ": the experimental setting \"Several inputs per relation\" is off (Settings > Experimental, or --multi-bind on).";
        }

        // The warning per relation of a game that keeps one input per action: only the main bind is exported.
        public static string LimitedNote(string gameName, string relationName, int dropped, string reason)
        {
            return gameName + " keeps " + reason + ": relation '" + relationName + "' was exported with its main input only; " + dropped +
                   " extra input(s) not written.";
        }
    }
}
