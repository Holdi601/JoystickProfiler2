using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // A profile bind an export did not write because the game already uses that input (device, input and modifiers, in
    // that aircraft) for another function: "Only add" never deletes, so writing it would put two functions on one button.
    // Every exporter fills its result's SkippedTakenInputs with these (DCS: EngineDcsExportResult, IL-2:
    // EngineIl2ExportResult, Star Citizen: EngineStarCitizenExportResult, the registry games: GameIoResult); the export
    // commands hand them to ExportSkipMarks, which marks them in the profile (docs/export.md "Only add").
    public sealed class ExportSkippedBind
    {
        // Game id as the backups and the sync record name it (DCS, IL2Game, StarCitizen, MSFS2020, ...).
        public string Game { get; set; }
        // The aircraft (DCS plane, ship, ...) as the profile names it; empty for a game without aircraft scopes.
        public string Aircraft { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public List<string> Modifiers { get; set; }
        // The profile's relation and action.
        public string Relation { get; set; }
        public string ActionId { get; set; }
        public string Action { get; set; }
        // The function the game has on that input.
        public string GameActionId { get; set; }
        public string GameAction { get; set; }

        public ExportSkippedBind()
        {
            Game = Aircraft = Device = Input = Relation = ActionId = Action = GameActionId = GameAction = string.Empty;
            Modifiers = new List<string>();
        }

        // The modifiers as one text ("LCtrl+LAlt"), for exporters that keep them so; not serialized.
        [System.Web.Script.Serialization.ScriptIgnore]
        public string ModifiersText
        {
            get { return string.Join("+", Modifiers ?? new List<string>()); }
            set { Modifiers = (value ?? string.Empty).Split('+').Select(m => m.Trim()).Where(m => m.Length > 0).ToList(); }
        }

        public string Describe()
        {
            string action = Action.Length > 0 ? Action : ActionId;
            string game = GameAction.Length > 0 ? GameAction : GameActionId;
            return Relation + " (" + action + ") on " + Device + " " + (Modifiers.Count > 0 ? string.Join("+", Modifiers) + "+" : string.Empty) + Input +
                   (Aircraft.Length > 0 ? " in " + Aircraft : string.Empty) + ": the game uses that input for " + game + ".";
        }
    }

    // The profile's mark of such a bind ("not exported: input taken in the game by <action> (<date>)").
    public sealed class ExportSkipMark
    {
        public string Game { get; set; }
        public string Aircraft { get; set; }
        public string Relation { get; set; }
        public string ActionId { get; set; }
        public string Action { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public List<string> Modifiers { get; set; }
        public string GameActionId { get; set; }
        public string GameAction { get; set; }
        // yyyy-MM-dd of the export that skipped it.
        public string Date { get; set; }
        // "Keep as is": the Check shows no card for it (a later export with the same conflict keeps it dismissed).
        public bool Dismissed { get; set; }

        public ExportSkipMark()
        {
            Game = Aircraft = Relation = ActionId = Action = Device = Input = GameActionId = GameAction = Date = string.Empty;
            Modifiers = new List<string>();
        }
    }

    public sealed class ExportSkipMarksDocument
    {
        public List<ExportSkipMark> Marks { get; set; }
        public int Changed { get; set; }
        public List<string> Warnings { get; set; }

        public ExportSkipMarksDocument()
        {
            Marks = new List<ExportSkipMark>();
            Warnings = new List<string>();
        }
    }

    // The marks live in the profile (Pr0file.ExportSkipMarks, one JSON object per line). An export replaces the marks
    // of its game and aircraft with what it skipped this time: a bind that is written now, or whose input is free in the
    // game now, loses its mark by itself.
    internal static class ExportSkipMarks
    {
        // Set when an export changed the marks: the run saves the profile then (Program, like the vJoy slots).
        public static bool Changed;

        public static List<ExportSkipMark> All()
        {
            List<ExportSkipMark> marks = new List<ExportSkipMark>();
            foreach (string line in InternalDataManagement.ExportSkipMarks ?? new List<string>())
            {
                try
                {
                    ExportSkipMark mark = EngineSerialization.FromJson<ExportSkipMark>(line);
                    if (mark != null && !string.IsNullOrWhiteSpace(mark.Game))
                        marks.Add(Normalize(mark));
                }
                catch (Exception)
                {
                    // A broken line is dropped at the next change.
                }
            }
            return marks;
        }

        public static List<ExportSkipMark> List(string game)
        {
            return All()
                .Where(m => string.IsNullOrWhiteSpace(game) || string.Equals(m.Game, game, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Game, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Relation, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Aircraft, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // After an export of 'game' (not a dry run): the marks of the exported aircraft (null or empty: all of the game)
        // become the binds skipped this time. Returns how many marks changed.
        public static int Apply(string game, IEnumerable<string> exportedAircraft, IEnumerable<ExportSkippedBind> skipped)
        {
            if (string.IsNullOrWhiteSpace(game))
                return 0;
            HashSet<string> scope = exportedAircraft == null
                ? null
                : new HashSet<string>(exportedAircraft.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            if (scope != null && scope.Count == 0)
                scope = null;
            List<ExportSkipMark> all = All();
            List<ExportSkipMark> before = all.Where(m => InScope(m, game, scope)).ToList();
            List<ExportSkipMark> kept = all.Where(m => !InScope(m, game, scope)).ToList();
            Dictionary<string, ExportSkipMark> previous = new Dictionary<string, ExportSkipMark>(StringComparer.OrdinalIgnoreCase);
            foreach (ExportSkipMark mark in before)
                previous[Key(mark)] = mark;

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Dictionary<string, ExportSkipMark> now = new Dictionary<string, ExportSkipMark>(StringComparer.OrdinalIgnoreCase);
            foreach (ExportSkippedBind bind in skipped ?? Enumerable.Empty<ExportSkippedBind>())
            {
                if (bind == null)
                    continue;
                ExportSkipMark mark = Normalize(new ExportSkipMark
                {
                    Game = string.IsNullOrWhiteSpace(bind.Game) ? game : bind.Game,
                    Aircraft = bind.Aircraft,
                    Relation = bind.Relation,
                    ActionId = string.IsNullOrWhiteSpace(bind.ActionId) ? bind.Action : bind.ActionId,
                    Action = string.IsNullOrWhiteSpace(bind.Action) ? bind.ActionId : bind.Action,
                    Device = bind.Device,
                    Input = bind.Input,
                    Modifiers = bind.Modifiers == null ? new List<string>() : new List<string>(bind.Modifiers),
                    GameActionId = string.IsNullOrWhiteSpace(bind.GameActionId) ? bind.GameAction : bind.GameActionId,
                    GameAction = string.IsNullOrWhiteSpace(bind.GameAction) ? bind.GameActionId : bind.GameAction,
                    Date = today
                });
                ExportSkipMark old;
                if (previous.TryGetValue(Key(mark), out old))
                {
                    // The same conflict as before: its date and "Keep as is" stay.
                    mark.Date = string.IsNullOrWhiteSpace(old.Date) ? today : old.Date;
                    mark.Dismissed = old.Dismissed;
                }
                now[Key(mark)] = mark;
            }

            int changed = before.Count(m => !now.ContainsKey(Key(m))) + now.Values.Count(m => !previous.ContainsKey(Key(m)));
            if (changed == 0)
                return 0;
            kept.AddRange(now.Values);
            Store(kept);
            Changed = true;
            return changed;
        }

        // "Keep as is". Returns how many marks were dismissed.
        public static int Dismiss(string game, string relation, string aircraft)
        {
            List<ExportSkipMark> all = All();
            int count = 0;
            foreach (ExportSkipMark mark in all)
            {
                if (mark.Dismissed || !string.Equals(mark.Game, game, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(mark.Relation, relation ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(aircraft) && !string.Equals(mark.Aircraft, aircraft.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                mark.Dismissed = true;
                count++;
            }
            if (count > 0)
            {
                Store(all);
                Changed = true;
            }
            return count;
        }

        // engine export-marks list [--game <id>] | dismiss --game <id> --relation <name> [--aircraft <a>]
        public static string ToText(ExportSkipMarksDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Binds not exported (input taken in the game): " + doc.Marks.Count.ToString(CultureInfo.InvariantCulture));
            foreach (ExportSkipMark mark in doc.Marks)
            {
                sb.AppendLine("  - " + mark.Game + (mark.Aircraft.Length > 0 ? " / " + mark.Aircraft : string.Empty) + ": " + mark.Relation +
                              " (" + (mark.Action.Length > 0 ? mark.Action : mark.ActionId) + ") on " + mark.Device + " " +
                              (mark.Modifiers.Count > 0 ? string.Join("+", mark.Modifiers) + "+" : string.Empty) + mark.Input +
                              " - taken by " + (mark.GameAction.Length > 0 ? mark.GameAction : mark.GameActionId) + " (" + mark.Date + ")" +
                              (mark.Dismissed ? " [kept as is]" : string.Empty));
            }
            if (doc.Changed > 0)
                sb.AppendLine("Changed: " + doc.Changed.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool InScope(ExportSkipMark mark, string game, HashSet<string> scope)
        {
            return string.Equals(mark.Game, game, StringComparison.OrdinalIgnoreCase) && (scope == null || scope.Contains(mark.Aircraft));
        }

        private static string Key(ExportSkipMark mark)
        {
            return string.Join("\t", mark.Game, mark.Aircraft, mark.Relation, mark.ActionId, mark.Device, mark.Input,
                string.Join("+", mark.Modifiers.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)), mark.GameActionId).ToLowerInvariant();
        }

        private static ExportSkipMark Normalize(ExportSkipMark mark)
        {
            mark.Game = (mark.Game ?? string.Empty).Trim();
            mark.Aircraft = (mark.Aircraft ?? string.Empty).Trim();
            mark.Relation = (mark.Relation ?? string.Empty).Trim();
            mark.ActionId = (mark.ActionId ?? string.Empty).Trim();
            mark.Action = (mark.Action ?? string.Empty).Trim();
            mark.Device = (mark.Device ?? string.Empty).Trim();
            mark.Input = (mark.Input ?? string.Empty).Trim();
            mark.Modifiers = (mark.Modifiers ?? new List<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
            mark.GameActionId = (mark.GameActionId ?? string.Empty).Trim();
            mark.GameAction = (mark.GameAction ?? string.Empty).Trim();
            mark.Date = (mark.Date ?? string.Empty).Trim();
            return mark;
        }

        private static void Store(List<ExportSkipMark> marks)
        {
            InternalDataManagement.ExportSkipMarks = marks
                .Select(m => EngineSerialization.ToJson(m))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();
        }
    }
}
