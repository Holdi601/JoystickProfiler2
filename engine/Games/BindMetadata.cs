using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // JoyPro's own data of a bind that a game file cannot hold: the relation name, its groups (in their order), the
    // device alias, the modifiers with their names and switch flag, and the axis tuning (base and every scope). An
    // export keeps it next to the game file (IL-2: //JP: comments in jp.actions; registry games:
    // <config>.joypro-meta.json; DCS has its own .jp files), and an import restores the relation from it instead of
    // naming it after the action, like JoyPro 1 did with its .jp files and IL-2 comments.
    internal sealed class BindMetadata
    {
        public string RelationName { get; set; }
        public List<string> Groups { get; set; }
        public string Alias { get; set; }
        // Full modifier strings "name§device§key§switch" (empty: the game file's modifiers are used).
        public List<string> Reformers { get; set; }
        // AxisTuning.Format of the bind's base, and its scopes (scope -> format); axes only.
        public string BaseTuning { get; set; }
        public Dictionary<string, string> Overrides { get; set; }
        // How the game file read back right after JoyPro's export (FileTuningOf): if the file still reads so on import,
        // nothing was changed in the game and the stored tuning alone is the bind's.
        public string FileTuning { get; set; }
        // Experimental multi-bind: which input of the relation the bind is (1 = the main bind, 2.. = its extra binds).
        // Not a JSON member of the entry: the sidecar keeps it in "Inputs" only when a bind is an extra bind, and the IL-2
        // comment as a seventh field, so files without extra binds stay as they were.
        [System.Web.Script.Serialization.ScriptIgnore]
        public int Input { get; set; }

        public BindMetadata()
        {
            RelationName = string.Empty;
            Groups = new List<string>();
            Alias = string.Empty;
            Reformers = new List<string>();
            BaseTuning = string.Empty;
            Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FileTuning = string.Empty;
        }

        // A read binding's axis values in one comparable form (what GameProfileBridge.CreateBind makes of them).
        public static string FileTuningOf(GameInputBinding binding)
        {
            if (binding == null || !binding.IsAxis)
                return string.Empty;
            AxisTuning tuning = binding.Tuning != null ? binding.Tuning.Clone() : new AxisTuning();
            tuning.Inverted = binding.Inverted;
            if (binding.Tuning == null)
            {
                tuning.Deadzone = binding.Deadzone;
                tuning.SaturationX = binding.SaturationX;
                tuning.SaturationY = binding.SaturationY;
                tuning.Curvature = binding.Curvature == null || binding.Curvature.Count == 0 ? new List<double> { 0.0 } : new List<double>(binding.Curvature);
            }
            return tuning.Format();
        }

        public static BindMetadata FromRelation(Relation relation, Bind bind)
        {
            BindMetadata meta = new BindMetadata();
            if (relation == null)
                return meta;
            meta.RelationName = relation.NAME ?? string.Empty;
            meta.Groups = (relation.Groups ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (bind == null)
                return meta;
            meta.Input = MultiBind.NumberOf(relation, bind);
            meta.Alias = !string.IsNullOrWhiteSpace(bind.AliasJoystick)
                ? bind.AliasJoystick
                : (InternalDataManagement.JoystickAliases != null && bind.Joystick != null && InternalDataManagement.JoystickAliases.ContainsKey(bind.Joystick)
                    ? InternalDataManagement.JoystickAliases[bind.Joystick] ?? string.Empty
                    : string.Empty);
            meta.Reformers = bind.AllReformers == null ? new List<string>() : bind.AllReformers.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (relation.ISAXIS)
            {
                meta.BaseTuning = BindTuning.Base(bind).Format();
                if (bind.TuningOverrides != null)
                {
                    foreach (KeyValuePair<string, string> scope in bind.TuningOverrides)
                        meta.Overrides[scope.Key] = scope.Value;
                }
            }
            return meta;
        }

        public bool HasTuning
        {
            get { return !string.IsNullOrWhiteSpace(BaseTuning); }
        }

        // The modifiers stored for a bind, when they are the ones the game file has (same devices and keys).
        public List<string> ReformersMatching(string device, bool isAxis, string input, IEnumerable<string> fileReformers)
        {
            if (Reformers == null || Reformers.Count == 0)
                return null;
            string stored = GameProfileBridge.BuildSignature(device, isAxis, input, Reformers);
            string found = GameProfileBridge.BuildSignature(device, isAxis, input, fileReformers);
            return string.Equals(stored, found, StringComparison.OrdinalIgnoreCase) ? new List<string>(Reformers) : null;
        }

        // The stored axis tuning replaces the bind's: base values and every scope.
        public void ApplyTuning(Bind bind)
        {
            if (bind == null || !HasTuning)
                return;
            BindTuning.Set(bind, BindTuning.BaseScope, AxisTuning.Parse(BaseTuning));
            bind.TuningOverrides = Overrides == null || Overrides.Count == 0
                ? null
                : new Dictionary<string, string>(Overrides, StringComparer.OrdinalIgnoreCase);
        }

        // IL-2 comment form: name;groups;alias;modifiers;base;scopes, lists joined by ",", scopes as scope=format, every
        // value escaped (MetadataCodec). The first three fields are the old form (JoyPro 1 and earlier JoyPro 2).
        public string ToCommentFields()
        {
            return string.Join(";", new[]
            {
                MetadataCodec.Encode(RelationName),
                string.Join(",", (Groups ?? new List<string>()).Select(MetadataCodec.Encode)),
                MetadataCodec.Encode(Alias),
                string.Join(",", (Reformers ?? new List<string>()).Select(MetadataCodec.Encode)),
                MetadataCodec.Encode(BaseTuning),
                string.Join(",", (Overrides ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(o => MetadataCodec.Encode(o.Key) + "=" + MetadataCodec.Encode(o.Value))),
                Input > 1 ? Input.ToString(CultureInfo.InvariantCulture) : string.Empty
            }).TrimEnd(';');
        }

        public static BindMetadata FromCommentFields(string segment)
        {
            BindMetadata meta = new BindMetadata();
            string[] parts = (segment ?? string.Empty).Split(';');
            meta.RelationName = parts.Length > 0 ? MetadataCodec.Decode(parts[0]).Trim() : string.Empty;
            meta.Groups = parts.Length > 1 ? SplitList(parts[1]) : new List<string>();
            meta.Alias = parts.Length > 2 ? MetadataCodec.Decode(parts[2]).Trim() : string.Empty;
            meta.Reformers = parts.Length > 3 ? SplitList(parts[3]) : new List<string>();
            meta.BaseTuning = parts.Length > 4 ? MetadataCodec.Decode(parts[4]).Trim() : string.Empty;
            if (parts.Length > 5)
            {
                foreach (string item in parts[5].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = item.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string scope = MetadataCodec.Decode(item.Substring(0, eq)).Trim();
                    if (BindTuning.IsValidScope(scope) && scope != BindTuning.BaseScope)
                        meta.Overrides[scope] = MetadataCodec.Decode(item.Substring(eq + 1));
                }
            }
            int input;
            if (parts.Length > 6 && int.TryParse(parts[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out input))
                meta.Input = input;
            return meta;
        }

        private static List<string> SplitList(string raw)
        {
            return (raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => MetadataCodec.Decode(v).Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // Escapes the characters that separate metadata fields and lists (and the comment marker) as %XX.
    internal static class MetadataCodec
    {
        private const string Escaped = "%;,|/=\r\n";

        public static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (Escaped.IndexOf(c) >= 0)
                    sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // Only the escapes Encode writes are turned back (values written before the escaping keep any other "%").
        public static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('%') < 0)
                return value ?? string.Empty;
            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                int code;
                if (value[i] == '%' && i + 2 < value.Length &&
                    int.TryParse(value.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) &&
                    Escaped.IndexOf((char)code) >= 0)
                {
                    sb.Append((char)code);
                    i += 2;
                }
                else
                    sb.Append(value[i]);
            }
            return sb.ToString();
        }
    }

    // "<config file>.joypro-meta.json" next to a registry game's file: BindMetadata per exported bind, keyed by the
    // action and the physical input (GameProfileBridge.BuildSignature). A game that loads every file of the config's
    // folder with the config's extension (IGameScansConfigFolder, FreeSpace Open) gets "<config file>.joypro-meta".
    internal static class GameMetadataStore
    {
        public const string Suffix = ".joypro-meta.json";
        public const string PlainSuffix = ".joypro-meta";
        private const string KeySeparator = "\u001E";

        private sealed class Document
        {
            public string Game { get; set; }
            public string UpdatedUtc { get; set; }
            public Dictionary<string, BindMetadata> Binds { get; set; }
        }

        // The same with the input numbers of extra binds (experimental multi-bind): written only when the file has one,
        // read always (a file without "Inputs" reads as before; an older JoyPro ignores the member).
        private sealed class DocumentWithInputs
        {
            public string Game { get; set; }
            public string UpdatedUtc { get; set; }
            public Dictionary<string, BindMetadata> Binds { get; set; }
            public Dictionary<string, int> Inputs { get; set; }
        }

        private static string Serialize(string game, string updatedUtc, Dictionary<string, BindMetadata> binds)
        {
            Dictionary<string, BindMetadata> ordered = binds.OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Value);
            if (!ordered.Values.Any(m => m != null && m.Input > 1))
                return EngineSerialization.ToJson(new Document { Game = game, UpdatedUtc = updatedUtc, Binds = ordered });
            return EngineSerialization.ToJson(new DocumentWithInputs
            {
                Game = game,
                UpdatedUtc = updatedUtc,
                Binds = ordered,
                Inputs = ordered.Where(b => b.Value != null && b.Value.Input > 1).ToDictionary(b => b.Key, b => b.Value.Input)
            });
        }

        public static string Key(string actionId, string device, bool isAxis, string input, IEnumerable<string> reformers)
        {
            return (actionId ?? string.Empty).Trim().ToLowerInvariant() + KeySeparator + GameProfileBridge.BuildSignature(device, isAxis, input, reformers);
        }

        // The metadata file of a game's config file.
        public static string PathOf(IGameIntegration game, string configPath)
        {
            return GameConfigLocation.SidecarPath(configPath ?? string.Empty, game is IGameScansConfigFolder ? PlainSuffix : Suffix);
        }

        // The file an older JoyPro wrote for a game that scans its config folder ("<config>.joypro-meta.json"), or null.
        private static string LegacyPathOf(IGameIntegration game, string configPath)
        {
            if (!(game is IGameScansConfigFolder) || string.IsNullOrWhiteSpace(configPath) || GameConfigLocation.IsRegistryKey(configPath))
                return null;
            return GameConfigLocation.SidecarPath(configPath, Suffix);
        }

        public static Dictionary<string, BindMetadata> Load(IGameIntegration game, string configPath, List<string> warnings)
        {
            Dictionary<string, BindMetadata> binds = new Dictionary<string, BindMetadata>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(configPath))
                return binds;
            string path = PathOf(game, configPath);
            string legacy = LegacyPathOf(game, configPath);
            bool fromLegacy = false;
            if (!File.Exists(path) && legacy != null && File.Exists(legacy))
            {
                path = legacy;
                fromLegacy = true;
            }
            if (!File.Exists(path))
                return binds;
            try
            {
                DocumentWithInputs document = EngineSerialization.FromJson<DocumentWithInputs>(File.ReadAllText(path, Encoding.UTF8));
                if (document != null && document.Binds != null)
                {
                    foreach (KeyValuePair<string, BindMetadata> entry in document.Binds)
                    {
                        if (entry.Value != null && !string.IsNullOrWhiteSpace(entry.Value.RelationName))
                        {
                            int input;
                            if (document.Inputs != null && document.Inputs.TryGetValue(entry.Key, out input))
                                entry.Value.Input = input;
                            if (fromLegacy)
                                ((IGameScansConfigFolder)game).UpgradeLegacyMetadata(entry.Key, entry.Value);
                            binds[entry.Key] = entry.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Ignored unreadable JoyPro metadata file '" + path + "': " + ex.Message);
            }
            return binds;
        }

        // The exported binds' metadata replaces their entries; entries of other binds stay (an earlier export's bind
        // the game file may still have).
        public static void Save(IGameIntegration game, string configPath, IEnumerable<GameInputBinding> exported, IEnumerable<GameInputBinding> readBack, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return;
            SaveFile(game, configPath, exported, readBack, warnings);
            // The old file of a game that scans its config folder: its entries are in the new file now (none were left
            // when there is no new file), and the game would load it as one of its own (FreeSpace Open stopped at start
            // on players\<pilot>.json.joypro-meta.json).
            string legacy = LegacyPathOf(game, configPath);
            if (legacy != null && File.Exists(legacy))
            {
                try
                {
                    File.Delete(legacy);
                }
                catch (Exception ex)
                {
                    warnings?.Add("Could not remove JoyPro's old metadata file '" + legacy + "' (delete it by hand, the game reads it as its own file): " + ex.Message);
                }
            }
        }

        private static void SaveFile(IGameIntegration game, string configPath, IEnumerable<GameInputBinding> exported, IEnumerable<GameInputBinding> readBack, List<string> warnings)
        {
            Dictionary<string, string> fileTuning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding read in readBack ?? Enumerable.Empty<GameInputBinding>())
            {
                if (read != null && read.IsAxis)
                    fileTuning[Key(read.ActionId, read.Device, true, read.Input, read.Reformers)] = BindMetadata.FileTuningOf(read);
            }
            Dictionary<string, BindMetadata> binds = Load(game, configPath, warnings);
            int before = binds.Count;
            foreach (GameInputBinding binding in exported ?? Enumerable.Empty<GameInputBinding>())
            {
                Relation relation;
                if (binding == null || string.IsNullOrWhiteSpace(binding.RelationName) ||
                    !InternalDataManagement.AllRelations.TryGetValue(binding.RelationName, out relation) || relation == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(binding.RelationName))
                    bind = InternalDataManagement.AllBinds[binding.RelationName];
                // The relation's bind this binding came from (an extra bind of the experimental multi-bind, else the main).
                bind = MultiBind.BindOf(relation, bind, binding.Device, binding.Input);
                string key = Key(binding.ActionId, binding.Device, binding.IsAxis, binding.Input, binding.Reformers);
                BindMetadata meta = BindMetadata.FromRelation(relation, bind);
                string read;
                if (fileTuning.TryGetValue(key, out read))
                    meta.FileTuning = read;
                binds[key] = meta;
            }
            if (binds.Count == 0 && before == 0)
                return;
            string updatedUtc = DateTime.UtcNow.ToString("o");
            string path = PathOf(game, configPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            string json = Serialize(game.GameId, updatedUtc, binds);
            // Same content but the timestamp: no rewrite.
            if (File.Exists(path))
            {
                DocumentWithInputs stored = null;
                try { stored = EngineSerialization.FromJson<DocumentWithInputs>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception) { }
                if (stored != null)
                {
                    Dictionary<string, BindMetadata> storedBinds = stored.Binds ?? new Dictionary<string, BindMetadata>();
                    foreach (KeyValuePair<string, BindMetadata> entry in storedBinds)
                    {
                        int input;
                        if (entry.Value != null && stored.Inputs != null && stored.Inputs.TryGetValue(entry.Key, out input))
                            entry.Value.Input = input;
                    }
                    if (stored.Binds != null && Serialize(stored.Game, updatedUtc, storedBinds) == json)
                        return;
                }
            }
            GameBackups.Protect(path, null);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
    }
}
