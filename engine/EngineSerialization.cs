using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace JoyPro.Engine
{
    internal static class EngineSerialization
    {
        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 512;
            return serializer;
        }

        // ASCII only: every other character as \uXXXX (only strings hold them), so the JSON survives any output code page
        // (a console's best-fit mapping makes '"' of a title's curly quotes and breaks the document).
        public static string ToJson<T>(T value)
        {
            return AsciiOnly(CreateSerializer().Serialize(value));
        }

        internal static string AsciiOnly(string json)
        {
            if (json == null)
                return null;
            int first = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] > 0x7E)
                {
                    first = i;
                    break;
                }
            }
            if (first < 0)
                return json;
            StringBuilder sb = new StringBuilder(json.Length + 64);
            sb.Append(json, 0, first);
            for (int i = first; i < json.Length; i++)
            {
                char c = json[i];
                if (c <= 0x7E)
                    sb.Append(c);
                else
                    sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // JSON for the console: every character outside printable ASCII as \uXXXX. The engine's stdout uses the
        // console's code page (the GUI reads it the same way), which has no curly quotes, dashes or most accents; a
        // best-fit mapping turned a game text's “ into " and broke the JSON (the MSFS catalog of the relation editor
        // listed nothing). Escaped, the JSON reads the same in every code page.
        public static string ForConsole(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json ?? string.Empty;
            int first = 0;
            while (first < json.Length && json[first] < 0x7F)
                first++;
            if (first == json.Length)
                return json;
            StringBuilder sb = new StringBuilder(json.Length + 64);
            sb.Append(json, 0, first);
            for (int i = first; i < json.Length; i++)
            {
                char c = json[i];
                if (c < 0x7F)
                    sb.Append(c);
                else
                    sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // A payload that is JSON (an object or an array), not text.
        public static bool LooksLikeJson(string payload)
        {
            if (string.IsNullOrEmpty(payload))
                return false;
            foreach (char c in payload)
            {
                if (char.IsWhiteSpace(c))
                    continue;
                return c == '{' || c == '[';
            }
            return false;
        }

        public static T FromJson<T>(string json)
        {
            return CreateSerializer().Deserialize<T>(json);
        }

        public static string ToText(EngineRelationsDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            int count = doc.Relations == null ? 0 : doc.Relations.Count;
            sb.AppendLine("Relations: " + count);
            if (doc.Relations == null)
                return sb.ToString();

            for (int i = 0; i < doc.Relations.Count; i++)
            {
                EngineRelationDto relation = doc.Relations[i];
                if (relation == null || string.IsNullOrWhiteSpace(relation.Name))
                    continue;

                string mode = relation.IsAxis ? "axis" : "button";
                string bindLabel = relation.Bind == null
                    ? "unbound"
                    : Safe(relation.Bind.Joystick) + "::" + Safe(BindKey(relation.Bind));
                int modCount = relation.Bind == null || relation.Bind.AllReformers == null ? 0 : relation.Bind.AllReformers.Count;
                int planeCount = RelationPlaneCount(relation);
                List<string> games = RelationGames(relation);
                sb.AppendLine(
                    relation.Name +
                    " | type=" + mode +
                    " | bind=" + bindLabel +
                    " | mods=" + modCount +
                    " | games=" + FormatList(games) +
                    " | planes=" + planeCount +
                    " | groups=" + FormatGroups(relation.Groups));
            }
            return sb.ToString();
        }

        public static string ToText(EngineBindsDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            int count = doc.Binds == null ? 0 : doc.Binds.Count;
            sb.AppendLine("Binds: " + count);
            if (doc.Binds == null)
                return sb.ToString();

            for (int i = 0; i < doc.Binds.Count; i++)
            {
                EngineBindDto bind = doc.Binds[i];
                if (bind == null || string.IsNullOrWhiteSpace(bind.RelationName))
                    continue;

                string mode = bind.IsAxis ? "axis" : "button";
                string key = BindKey(bind);
                int modCount = bind.AllReformers == null ? 0 : bind.AllReformers.Count;
                string mods = modCount == 0 ? "<none>" : FormatList(bind.AllReformers);
                sb.AppendLine(
                    bind.RelationName +
                    " | type=" + mode +
                    " | device=" + Safe(bind.Joystick) +
                    " | input=" + Safe(key) +
                    " | modifiers(" + modCount + ")=" + mods);
            }
            return sb.ToString();
        }

        public static string ToText(EngineRelationDto relation)
        {
            if (relation == null)
                return "Relation: <null>";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Relation: " + relation.Name);
            sb.AppendLine("Type: " + (relation.IsAxis ? "Axis" : "Button"));
            sb.AppendLine("Groups: " + FormatGroups(relation.Groups));
            sb.AppendLine("Games: " + FormatList(RelationGames(relation)));
            sb.AppendLine("Planes Covered: " + RelationPlaneCount(relation));
            if (relation.Bind == null)
            {
                sb.AppendLine("Bind: <none>");
            }
            else
            {
                sb.AppendLine("Bind: " + FormatBindLine(relation.Bind));
                sb.AppendLine("Bind Reformers: " + FormatList(relation.Bind.AllReformers));
                sb.AppendLine("Bind Inverted/Slider/DoubleTap: " + FormatNullableBool(relation.Bind.Inverted) + " / " + FormatNullableBool(relation.Bind.Slider) + " / " + relation.Bind.DoubleTap.ToString().ToLowerInvariant());
                sb.AppendLine("Bind Curvature: " + FormatCurvature(relation.Bind.Curvature));
                sb.AppendLine("Bind Deadzone/SatX/SatY: " + relation.Bind.Deadzone + " / " + relation.Bind.SaturationX + " / " + relation.Bind.SaturationY);
            }

            int nodeCount = relation.Nodes == null ? 0 : relation.Nodes.Count;
            sb.AppendLine("Nodes: " + nodeCount);
            if (relation.Nodes != null)
            {
                for (int i = 0; i < relation.Nodes.Count; i++)
                {
                    EngineRelationItemDto node = relation.Nodes[i];
                    sb.AppendLine("  [" + (i + 1) + "] game=" + Safe(node.Game) + ", id=" + Safe(node.Id) + ", aircraft=" + FormatAircraft(node.Aircraft));
                    // "      stored-title<TAB>plane<TAB>title<TAB>category": what the profile stored (GUI fallback title).
                    foreach (KeyValuePair<string, string> stored in (node.StoredTitles ?? new Dictionary<string, string>()).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        string category;
                        if (node.StoredCategories == null || !node.StoredCategories.TryGetValue(stored.Key, out category))
                            category = string.Empty;
                        sb.AppendLine("      stored-title\t" + OneLine(stored.Key) + "\t" + OneLine(stored.Value) + "\t" + OneLine(category));
                    }
                }
            }
            return sb.ToString();
        }

        public static string ToText(EngineBindWithRelationDto bindWithRelation)
        {
            if (bindWithRelation == null)
                return "Bind: <null>";

            StringBuilder sb = new StringBuilder();
            if (bindWithRelation.Bind == null)
            {
                sb.AppendLine("Bind: <none>");
            }
            else
            {
                sb.AppendLine("Bind: " + FormatBindLine(bindWithRelation.Bind));
                sb.AppendLine("Bind Reformers: " + FormatList(bindWithRelation.Bind.AllReformers));
                sb.AppendLine("Bind Inverted/Slider/DoubleTap: " + FormatNullableBool(bindWithRelation.Bind.Inverted) + " / " + FormatNullableBool(bindWithRelation.Bind.Slider) + " / " + bindWithRelation.Bind.DoubleTap.ToString().ToLowerInvariant());
                sb.AppendLine("Bind Curvature: " + FormatCurvature(bindWithRelation.Bind.Curvature));
                sb.AppendLine("Bind Deadzone/SatX/SatY: " + bindWithRelation.Bind.Deadzone + " / " + bindWithRelation.Bind.SaturationX + " / " + bindWithRelation.Bind.SaturationY);
            }

            sb.AppendLine();
            sb.AppendLine(ToText(bindWithRelation.Relation));
            return sb.ToString();
        }

        public static EngineRelationsDocument ExportRelations()
        {
            EngineRelationsDocument doc = new EngineRelationsDocument();
            IEnumerable<KeyValuePair<string, Relation>> source = InternalDataManagement.AllRelations.OrderBy(k => k.Key);
            foreach (KeyValuePair<string, Relation> entry in source)
            {
                Relation relation = entry.Value;
                EngineRelationDto relationDto = new EngineRelationDto();
                relationDto.Name = relation.NAME;
                relationDto.IsAxis = relation.ISAXIS;
                relationDto.Groups = relation.Groups == null ? new List<string>() : new List<string>(relation.Groups);
                relationDto.Function = string.IsNullOrWhiteSpace(relation.Function) ? null : relation.Function;
                relationDto.Nodes = new List<EngineRelationItemDto>();
                if (relation.NODES != null)
                {
                    for (int i = 0; i < relation.NODES.Count; i++)
                    {
                        RelationItem item = relation.NODES[i];
                        EngineRelationItemDto itemDto = new EngineRelationItemDto();
                        itemDto.Id = item.ID;
                        itemDto.Game = string.IsNullOrWhiteSpace(item.Game) ? "DCS" : item.Game;
                        itemDto.Aircraft = item.AIRCRAFT == null
                            ? new Dictionary<string, bool>()
                            : new Dictionary<string, bool>(item.AIRCRAFT);
                        relationDto.Nodes.Add(itemDto);
                    }
                }

                if (InternalDataManagement.AllBinds.ContainsKey(relation.NAME))
                {
                    relationDto.Bind = ToBindDto(relation.NAME, relation.ISAXIS, InternalDataManagement.AllBinds[relation.NAME]);
                }

                doc.Relations.Add(relationDto);
            }
            return doc;
        }

        // "relations show": the stored titles of the relation's nodes (only there: the list of every relation stays small).
        public static void AddStoredTitles(EngineRelationDto relationDto)
        {
            Relation relation;
            if (relationDto == null || relationDto.Nodes == null || InternalDataManagement.AllRelations == null ||
                !InternalDataManagement.AllRelations.TryGetValue(relationDto.Name ?? string.Empty, out relation) || relation == null || relation.NODES == null)
                return;
            for (int i = 0; i < relationDto.Nodes.Count && i < relation.NODES.Count; i++)
                AddStoredTitles(relation.NODES[i], relationDto.Nodes[i]);
        }

        // The stored title and category of every active aircraft of a node; DCS localization keys become DCS's English
        // text (or a readable form), raw ids are left out.
        private static void AddStoredTitles(RelationItem item, EngineRelationItemDto itemDto)
        {
            if (item == null || item.AIRCRAFT == null)
                return;
            bool dcs = string.IsNullOrWhiteSpace(item.Game) || string.Equals(item.Game, "DCS", StringComparison.OrdinalIgnoreCase);
            IDictionary<string, string> english = null;
            foreach (KeyValuePair<string, bool> plane in item.AIRCRAFT)
            {
                if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key))
                    continue;
                string title = null, category = null;
                if (dcs)
                {
                    DCSInput input = (item.AllInputs ?? new DCSInput[0]).FirstOrDefault(x => x != null && string.Equals((x.Plane ?? string.Empty).Trim(), plane.Key.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (input != null)
                    {
                        if (english == null && (DcsCommandNames.IsRawTitle(input.Title, item.ID) || DcsCommandNames.HasLocalizationKey(input.Cat)))
                            english = InternalDataManagement.DcsEnglishNames();
                        title = DcsCommandNames.Resolve(input.Title, item.ID, english);
                        category = DcsCommandNames.ResolveCategory(input.Cat, english);
                    }
                }
                else
                {
                    OtherGameInput input = (item.OtherInputs ?? new OtherGameInput[0]).FirstOrDefault(x => x != null && string.Equals((x.Plane ?? string.Empty).Trim(), plane.Key.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (input != null && !string.IsNullOrWhiteSpace(input.Title) && !string.Equals(input.Title.Trim(), (item.ID ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        title = input.Title.Trim();
                        category = input.Category;
                    }
                }
                if (!string.IsNullOrWhiteSpace(title))
                {
                    itemDto.StoredTitles[plane.Key] = title;
                    itemDto.StoredCategories[plane.Key] = (category ?? string.Empty).Trim();
                }
            }
        }

        private static string OneLine(string value)
        {
            return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public static EngineBindsDocument ExportBinds()
        {
            EngineBindsDocument doc = new EngineBindsDocument();
            IEnumerable<KeyValuePair<string, Bind>> source = InternalDataManagement.AllBinds.OrderBy(k => k.Key);
            foreach (KeyValuePair<string, Bind> entry in source)
            {
                bool isAxis = InternalDataManagement.AllRelations.ContainsKey(entry.Key) && InternalDataManagement.AllRelations[entry.Key].ISAXIS;
                doc.Binds.Add(ToBindDto(entry.Key, isAxis, entry.Value));
            }
            return doc;
        }

        public static void ImportRelations(EngineRelationsDocument doc)
        {
            if (doc == null)
                throw new InvalidDataException("Missing relation document.");
            if (doc.Relations == null)
                throw new InvalidDataException("Missing relation payload.");

            Dictionary<string, Relation> relations = new Dictionary<string, Relation>();
            Dictionary<string, Bind> binds = new Dictionary<string, Bind>();

            for (int i = 0; i < doc.Relations.Count; i++)
            {
                EngineRelationDto relationDto = doc.Relations[i];
                if (relationDto == null || string.IsNullOrWhiteSpace(relationDto.Name))
                    continue;

                string relationKey = relationDto.Name.Trim();
                while (relations.ContainsKey(relationKey))
                {
                    relationKey = relationKey + "1";
                }

                Relation relation = new Relation();
                relation.NAME = relationKey;
                relation.ISAXIS = relationDto.IsAxis;
                relation.Groups = relationDto.Groups == null
                    ? new List<string>()
                    : relationDto.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
                relation.Function = string.IsNullOrWhiteSpace(relationDto.Function) ? null : relationDto.Function.Trim();
                relation.NODES = new List<RelationItem>();

                if (relationDto.Nodes != null)
                {
                    for (int j = 0; j < relationDto.Nodes.Count; j++)
                    {
                        EngineRelationItemDto nodeDto = relationDto.Nodes[j];
                        if (nodeDto == null || string.IsNullOrWhiteSpace(nodeDto.Id))
                            continue;
                        RelationItem item = new RelationItem();
                        item.ID = nodeDto.Id;
                        item.Game = string.IsNullOrWhiteSpace(nodeDto.Game) ? "DCS" : nodeDto.Game;
                        item.AIRCRAFT = nodeDto.Aircraft == null
                            ? new Dictionary<string, bool>()
                            : new Dictionary<string, bool>(nodeDto.Aircraft);
                        InternalDataManagement.MigratePlanelessNodeAircraft(item);
                        item.AllInputs = new DCSInput[0];
                        item.OtherInputs = new OtherGameInput[0];
                        relation.NODES.Add(item);
                    }
                }
                JoyPro.Engine.Games.LegacyGameIds.MigrateRelation(relation);

                relations.Add(relationKey, relation);

                if (relationDto.Bind != null)
                {
                    relationDto.Bind.RelationName = relationKey;
                    relationDto.Bind.IsAxis = relation.ISAXIS;
                    Bind bind = ToBind(relationDto.Bind, relation);
                    binds[relationKey] = bind;
                    relation.bind = bind;
                }
            }

            InternalDataManagement.AllRelations = relations;
            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.AllModifiers.Clear();
            InternalDataManagement.AllGroups.Clear();
            InternalDataManagement.GroupActivity.Clear();
            InternalDataManagement.JoystickActivity.Clear();
            RebuildGroupList();
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        public static void ImportBinds(EngineBindsDocument doc)
        {
            if (doc == null)
                throw new InvalidDataException("Missing bind document.");
            if (doc.Binds == null)
                throw new InvalidDataException("Missing bind payload.");

            Dictionary<string, Bind> binds = new Dictionary<string, Bind>();

            IEnumerable<KeyValuePair<string, Relation>> relationEntries = InternalDataManagement.AllRelations.ToList();
            foreach (KeyValuePair<string, Relation> relationEntry in relationEntries)
            {
                relationEntry.Value.bind = null;
            }

            for (int i = 0; i < doc.Binds.Count; i++)
            {
                EngineBindDto bindDto = doc.Binds[i];
                if (bindDto == null || string.IsNullOrWhiteSpace(bindDto.RelationName))
                    continue;

                string relationName = bindDto.RelationName.Trim();
                Relation relation;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName))
                {
                    relation = InternalDataManagement.AllRelations[relationName];
                }
                else
                {
                    relation = new Relation();
                    relation.NAME = relationName;
                    relation.ISAXIS = bindDto.IsAxis;
                    InternalDataManagement.AllRelations.Add(relationName, relation);
                }

                bindDto.IsAxis = relation.ISAXIS;
                Bind bind = ToBind(bindDto, relation);
                binds[relationName] = bind;
                relation.bind = bind;
            }

            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.AllModifiers.Clear();
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static void RebuildGroupList()
        {
            for (int i = InternalDataManagement.AllGroups.Count - 1; i >= 0; i--)
            {
                InternalDataManagement.AllGroups.RemoveAt(i);
            }

            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation.Groups == null)
                    continue;

                for (int i = 0; i < relation.Groups.Count; i++)
                {
                    string group = relation.Groups[i];
                    if (string.IsNullOrWhiteSpace(group))
                        continue;
                    if (!InternalDataManagement.AllGroups.Contains(group))
                        InternalDataManagement.AllGroups.Add(group);
                }
            }
        }

        private static EngineBindDto ToBindDto(string relationName, bool isAxis, Bind bind)
        {
            EngineBindDto dto = new EngineBindDto();
            dto.RelationName = relationName;
            dto.IsAxis = isAxis;
            dto.Joystick = bind.Joystick;
            dto.JAxis = bind.JAxis;
            dto.JButton = bind.JButton;
            dto.Inverted = bind.Inverted;
            dto.Slider = bind.Slider;
            dto.Deadzone = bind.Deadzone;
            dto.Curvature = bind.Curvature == null ? new List<double>() : new List<double>(bind.Curvature);
            dto.SaturationX = bind.SaturationX;
            dto.SaturationY = bind.SaturationY;
            dto.AllReformers = bind.AllReformers == null ? new List<string>() : new List<string>(bind.AllReformers);
            dto.AdditionalImportInfo = bind.AdditionalImportInfo;
            dto.AliasJoystick = bind.AliasJoystick;
            dto.DoubleTap = bind.doubleTap;
            dto.TuningOverrides = bind.TuningOverrides == null ? null : new Dictionary<string, string>(bind.TuningOverrides, StringComparer.OrdinalIgnoreCase);
            return dto;
        }

        private static Bind ToBind(EngineBindDto dto, Relation relation)
        {
            Bind bind = new Bind(relation);
            bind.Joystick = dto.Joystick == null ? string.Empty : dto.Joystick;
            bind.JoyBackup = bind.Joystick;
            bind.JAxis = dto.JAxis == null ? string.Empty : dto.JAxis;
            bind.JButton = dto.JButton == null ? string.Empty : dto.JButton;
            bind.Inverted = dto.Inverted;
            bind.Slider = dto.Slider;
            bind.Deadzone = dto.Deadzone;
            bind.Curvature = dto.Curvature == null || dto.Curvature.Count == 0
                ? new List<double>() { 0.0 }
                : new List<double>(dto.Curvature);
            bind.SaturationX = dto.SaturationX;
            bind.SaturationY = dto.SaturationY;
            bind.AllReformers = dto.AllReformers == null ? new List<string>() : new List<string>(dto.AllReformers);
            bind.AdditionalImportInfo = dto.AdditionalImportInfo == null ? string.Empty : dto.AdditionalImportInfo;
            bind.AliasJoystick = dto.AliasJoystick == null ? string.Empty : dto.AliasJoystick;
            bind.doubleTap = dto.DoubleTap;
            bind.TuningOverrides = dto.TuningOverrides == null || dto.TuningOverrides.Count == 0 ? null : new Dictionary<string, string>(dto.TuningOverrides, StringComparer.OrdinalIgnoreCase);
            JoyPro.Engine.Games.LegacyGameIds.MigrateTuningScopes(bind.TuningOverrides);
            bind.Rl = relation;
            relation.bind = bind;
            return bind;
        }

        private static string FormatBindLine(EngineBindDto bind)
        {
            string key = BindKey(bind);
            string mode = bind.IsAxis ? "axis" : "button";
            return "relation=" + Safe(bind.RelationName) + ", joystick=" + Safe(bind.Joystick) + ", input=" + Safe(key) + ", mode=" + mode;
        }

        private static string BindKey(EngineBindDto bind)
        {
            if (bind == null)
                return string.Empty;
            if (!string.IsNullOrWhiteSpace(bind.JButton))
                return bind.JButton;
            if (!string.IsNullOrWhiteSpace(bind.JAxis))
                return bind.JAxis;
            return string.Empty;
        }

        private static string FormatGroups(List<string> groups)
        {
            return FormatList(groups);
        }

        private static string FormatList(List<string> items)
        {
            if (items == null || items.Count == 0)
                return "<none>";
            List<string> clean = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
            if (clean.Count == 0)
                return "<none>";
            return string.Join(", ", clean);
        }

        private static string FormatCurvature(List<double> curvature)
        {
            if (curvature == null || curvature.Count == 0)
                return "<none>";
            return string.Join(", ", curvature.Select(v => v.ToString()));
        }

        private static string FormatNullableBool(bool? value)
        {
            if (!value.HasValue)
                return "<null>";
            return value.Value ? "true" : "false";
        }

        private static string FormatAircraft(Dictionary<string, bool> aircraft)
        {
            if (aircraft == null || aircraft.Count == 0)
                return "<none>";

            List<string> active = aircraft
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .OrderBy(k => k)
                .ToList();

            if (active.Count == 0)
                return "<none>";
            return string.Join(", ", active);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
        }

        private static List<string> RelationGames(EngineRelationDto relation)
        {
            if (relation == null || relation.Nodes == null)
                return new List<string>();

            return relation.Nodes
                .Where(node => node != null)
                .Select(node => string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game)
                .Where(game => !string.IsNullOrWhiteSpace(game))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(game => game)
                .ToList();
        }

        private static int RelationPlaneCount(EngineRelationDto relation)
        {
            if (relation == null || relation.Nodes == null)
                return 0;

            HashSet<string> covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < relation.Nodes.Count; i++)
            {
                EngineRelationItemDto node = relation.Nodes[i];
                if (node == null || node.Aircraft == null)
                    continue;

                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                foreach (KeyValuePair<string, bool> plane in node.Aircraft)
                {
                    if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key))
                        continue;
                    covered.Add(game + "::" + plane.Key);
                }
            }

            return covered.Count;
        }
    }
}
