using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using JoyPro.DeviceReference;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games;
using JoyPro.Engine.VJoy;

namespace JoyPro.Engine
{
    // engine games support [--markdown | --html] [--notes <file>]: how much JoyPro supports each game
    // (docs/games/support.md). The feature matrix comes from the code: the game registry and the interfaces each
    // integration implements, the axis and settings models, the config watcher, the telemetry sources, the vJoy / Xbox
    // modes, the input limits, the function tables (Auto Assign, setup wizard) and the cockpit database. Only what code
    // cannot know comes from a hand-kept file (docs/games/support-notes.json, built into engine.exe): categories, bind
    // files, anti-cheat, verification results, open points, the documented-only titles and footnotes for partial cells.
    // A note may qualify a cell (✓ -> ◐, ✗ -> ◐ or —) but never claim more than the code does.
    internal static class EngineGameSupport
    {
        public const string Yes = "✓";
        public const string Partial = "◐";
        public const string No = "✗";
        public const string NotApplicable = "—";

        public const string NotesResource = "JoyPro.Engine.GameSupportNotes.json";

        internal sealed class Column
        {
            public string Key;
            public string Header;
            public string Meaning;
        }

        public static readonly Column[] Columns =
        {
            new Column { Key = "import", Header = "Import", Meaning = "reads the binds made in the game into a profile" },
            new Column { Key = "export", Header = "Export", Meaning = "writes binds into the game's files (modes clean, add, merge, merge-overwrite)" },
            new Column { Key = "keyboard", Header = "Keyboard", Meaning = "keyboard binds on request (--include-keyboard)" },
            new Column { Key = "modifiers", Header = "Modifiers", Meaning = "binds with modifier buttons" },
            new Column { Key = "refresh", Header = "List refresh", Meaning = "builds the command list on the user's PC from the installed game (refresh-db)" },
            new Column { Key = "watcher", Header = "Watcher", Meaning = "config watcher: engine sync status / diff / apply" },
            new Column { Key = "tuning", Header = "Axis tuning", Meaning = "curves and dead zones written in the game's own response model" },
            new Column { Key = "settings", Header = "Game settings", Meaning = "game-wide control settings (engine ffb set --device *)" },
            new Column { Key = "ffb", Header = "In-game FFB", Meaning = "the game's own force feedback settings (DCS FF Tune, IL-2 feedback power ...)" },
            new Column { Key = "telemetry", Header = "Telemetry FFB", Meaning = "JoyPro's force feedback from the game's telemetry (JoyPro.Ffb)" },
            new Column { Key = "vjoy", Header = "vJoy mode", Meaning = "virtual joystick (engine vjoy mode <game> virtual)" },
            new Column { Key = "xbox", Header = "Xbox pad", Meaning = "virtual Xbox 360 pad (engine vjoy mode <game> xbox)" },
            new Column { Key = "functions", Header = "Virtual functions", Meaning = "engine vjoy functions for the game" },
            new Column { Key = "wizard", Header = "Setup wizard / Auto Assign", Meaning = "JoyPro's function table for the game (DB\\Functions\\<game>.tsv), used by Auto Assign and the setup wizard" },
            new Column { Key = "cockpits", Header = "Cockpits", Meaning = "cockpits of the game's aircraft in JoyPro's cockpit database (DB\\Cockpits)" },
            new Column { Key = "layout", Header = "Cheat sheet", Meaning = "layout pictures / cheat sheets per game and aircraft (engine layout render)" },
            new Column { Key = "e2e", Header = "In-game test", Meaning = "JoyPro's in-game end-to-end test: result of the best run" }
        };

        // ---- what the code says ----------------------------------------------------------------------------------

        internal sealed class Cell
        {
            public string Mark { get; set; }
            public string Detail { get; set; }
            public string Note { get; set; }

            public Cell()
            {
                Mark = Detail = Note = string.Empty;
            }
        }

        internal sealed class Row
        {
            public string GameId { get; set; }
            public string Title { get; set; }
            public string Category { get; set; }
            public string Doc { get; set; }
            // native | virtual | xbox | documented
            public string Level { get; set; }
            public bool InEngine { get; set; }
            public bool Registry { get; set; }
            public bool Experimental { get; set; }
            public bool ExclusiveFlightSim { get; set; }
            public List<string> Aliases { get; set; }
            public string Command { get; set; }
            public string AxisModel { get; set; }
            public List<string> Settings { get; set; }
            public List<string> FfbSettings { get; set; }
            public int Functions { get; set; }
            public int Cockpits { get; set; }
            public int MaxJoysticks { get; set; }
            public string SuggestedMode { get; set; }
            public string InputLimit { get; set; }
            public string InputLimitConfidence { get; set; }
            public string BindFile { get; set; }
            public string ListSource { get; set; }
            public string ListSize { get; set; }
            public string Devices { get; set; }
            public string AntiCheat { get; set; }
            public string Verification { get; set; }
            public string TestResult { get; set; }
            public string TestDate { get; set; }
            public string TestNote { get; set; }
            public string Open { get; set; }
            public Dictionary<string, Cell> Cells { get; set; }

            public Row()
            {
                GameId = Title = Category = Doc = Level = Command = AxisModel = SuggestedMode = InputLimit = InputLimitConfidence = string.Empty;
                BindFile = ListSource = ListSize = Devices = AntiCheat = Verification = TestResult = TestDate = TestNote = Open = string.Empty;
                Aliases = new List<string>();
                Settings = new List<string>();
                FfbSettings = new List<string>();
                MaxJoysticks = -1;
                Cells = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
            }
        }

        internal sealed class Category
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Note { get; set; }
        }

        internal sealed class Report
        {
            public string Updated { get; set; }
            public string NotesSource { get; set; }
            public List<Category> Categories { get; set; }
            public List<Row> Rows { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> Errors { get; set; }

            public Report()
            {
                Updated = NotesSource = string.Empty;
                Categories = new List<Category>();
                Rows = new List<Row>();
                Warnings = new List<string>();
                Errors = new List<string>();
            }
        }

        private static readonly string[] BuiltIn = { "DCS", "IL2Game", "StarCitizen" };

        public const string GenericXboxNote = "Every game can be switched to the virtual Xbox pad, but JoyPro knows no pad layout of this game: " +
                                              "it puts your inputs on free pad controls and you bind those in the game (rarely needed for a game that reads joysticks).";

        // The engine's games: DCS, IL-2 Great Battles and Star Citizen (own code paths) and every registry game.
        public static List<Row> CodeRows(List<string> warnings)
        {
            List<Row> rows = new List<Row>();
            HashSet<string> watched = new HashSet<string>(EngineSync.AllGames().Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
            HashSet<string> telemetry = new HashSet<string>(EngineFfbTelemetry.Games.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
            HashSet<string> vjoyGames = new HashSet<string>(EngineVJoy.GameIds(), StringComparer.OrdinalIgnoreCase);
            CockpitCatalog cockpits = null;
            try
            {
                cockpits = Program.LoadCockpitCatalog();
            }
            catch (Exception ex)
            {
                warnings.Add("Cockpit database not read: " + ex.Message);
            }

            List<KeyValuePair<string, IGameIntegration>> games = BuiltIn.Select(id => new KeyValuePair<string, IGameIntegration>(id, null)).ToList();
            games.AddRange(GameRegistry.All.Select(g => new KeyValuePair<string, IGameIntegration>(g.GameId, g)));
            foreach (KeyValuePair<string, IGameIntegration> entry in games)
            {
                string id = entry.Key;
                IGameIntegration game = entry.Value;
                Row row = new Row { GameId = id, InEngine = true, Registry = game != null, Level = "native" };
                row.Title = game != null ? game.DisplayName : EngineVJoy.GameName(id);
                row.Experimental = game != null && game.Experimental;
                row.ExclusiveFlightSim = game == null ? id != "StarCitizen" : game.ExclusiveFlightSim;
                row.Aliases = game != null ? game.Aliases.ToList() : new List<string>();
                row.Command = game != null ? "engine " + (game.Aliases.Count > 0 ? game.Aliases[0] : game.GameId.ToLowerInvariant())
                    : id == "DCS" ? "engine dcs" : id == "IL2Game" ? "engine il2" : "engine starcitizen";

                IGameDeviceLimit deviceLimit = game as IGameDeviceLimit;
                if (deviceLimit != null)
                {
                    row.MaxJoysticks = deviceLimit.MaxJoysticks;
                    row.SuggestedMode = deviceLimit.SuggestedMode;
                    row.Level = deviceLimit.SuggestedMode == VJoySettings.ModeXbox ? "xbox" : "virtual";
                }
                GameInputLimit limit = GameInputLimits.For(id);
                if (limit.Known)
                {
                    row.InputLimit = limit.Describe();
                    row.InputLimitConfidence = limit.Confidence;
                }

                AxisModel axisModel = AxisModels.ForGame(id);
                row.AxisModel = axisModel.Id == AxisModels.Generic ? string.Empty : axisModel.Id;
                FfbModel ffbModel = FfbSettings.ForGame(SettingsGame(game, id));
                if (ffbModel != null)
                {
                    foreach (FfbParameter parameter in ffbModel.Parameters)
                    {
                        if (IsForceFeedback(parameter))
                            row.FfbSettings.Add(parameter.Label);
                        else if (parameter.GameWide)
                            row.Settings.Add(parameter.Label);
                    }
                }
                row.Functions = FunctionRows(id);
                row.Cockpits = cockpits == null ? 0 : CockpitCount(cockpits, id);

                Set(row, "import", Yes, string.Empty);
                Set(row, "export", Yes, string.Empty);
                Set(row, "keyboard", KeyboardMark(id, game), string.Empty);
                Set(row, "modifiers", ModifierMark(id, game), string.Empty);
                Set(row, "refresh", game == null || game.SupportsCatalogRefresh ? Yes : No, string.Empty);
                Set(row, "watcher", watched.Contains(id) ? Yes : No, string.Empty);
                Set(row, "tuning", row.AxisModel.Length > 0 ? Yes : No, row.AxisModel);
                bool settingsCode = game is IGameSettingsFile || game is IGameSettingsReader || game is IGameSettingsExport;
                Set(row, "settings", row.Settings.Count > 0 || settingsCode ? Yes : No, string.Empty);
                Set(row, "ffb", row.FfbSettings.Count > 0 ? Yes : No, string.Empty);
                Set(row, "telemetry", telemetry.Contains(id) ? Yes : No, string.Empty);
                bool xboxLayout = game is IGameGamepadLayout || (deviceLimit != null && deviceLimit.SuggestedMode == VJoySettings.ModeXbox);
                Set(row, "vjoy", !vjoyGames.Contains(id) ? No : deviceLimit != null && deviceLimit.MaxJoysticks == 0 ? NotApplicable : Yes, string.Empty);
                Set(row, "xbox", !vjoyGames.Contains(id) ? No : xboxLayout ? Yes : Partial, string.Empty);
                if (row.Cells["xbox"].Mark == Partial)
                    row.Cells["xbox"].Note = GenericXboxNote;
                Set(row, "functions", vjoyGames.Contains(id) ? Yes : No, string.Empty);
                Set(row, "wizard", row.Functions > 0 ? Yes : No, row.Functions > 0 ? row.Functions.ToString(CultureInfo.InvariantCulture) : string.Empty);
                Set(row, "cockpits", row.Cockpits > 0 ? Yes : No, row.Cockpits > 0 ? row.Cockpits.ToString(CultureInfo.InvariantCulture) : string.Empty);
                Set(row, "layout", Yes, string.Empty);
                Set(row, "e2e", No, string.Empty);
                rows.Add(row);
            }
            return rows;
        }

        private static void Set(Row row, string key, string mark, string detail)
        {
            row.Cells[key] = new Cell { Mark = mark, Detail = detail ?? string.Empty };
        }

        // The game whose settings model holds a game's settings (VTOL VR native: VTOLVR).
        private static string SettingsGame(IGameIntegration game, string id)
        {
            IGameSettingsSource source = game as IGameSettingsSource;
            return source != null && !string.IsNullOrWhiteSpace(source.SettingsGame) ? source.SettingsGame : id;
        }

        // A setting of the stick's force feedback (not pad rumble or VR haptics).
        private static bool IsForceFeedback(FfbParameter parameter)
        {
            string text = (parameter.Key + " " + parameter.Label).ToLowerInvariant();
            if (text.Contains("rumble") || text.Contains("vibration") || text.Contains("haptic"))
                return false;
            return text.Contains("force feedback") || text.Contains("feedback power") || text.Contains("ffb") || text.Contains("joyfx") ||
                   text.Contains("forcegain") || text.Contains("shak") || text.Contains("trimmer") || text.Contains("effect gain") ||
                   text.Contains("spring gain") || parameter.Key.StartsWith("ff", StringComparison.Ordinal);
        }

        // Rows of JoyPro's function table of the game (DB\Functions\<game>.tsv), the base of Auto Assign and the setup wizard.
        private static int FunctionRows(string gameId)
        {
            string folder = Environment.GetEnvironmentVariable("JOYPRO_FUNCTIONS_DB");
            string file = string.IsNullOrWhiteSpace(folder)
                ? Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Functions", gameId + ".tsv")
                : Path.Combine(folder, gameId + ".tsv");
            if (!File.Exists(file))
                return 0;
            return File.ReadLines(file, Encoding.UTF8).Count(l => l.Trim().Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));
        }

        private static int CockpitCount(CockpitCatalog catalog, string gameId)
        {
            // Aircraft (not variants) with at least one variant that names the game exactly (CockpitCatalog.GameMatches is
            // a prefix match: "FSX" would count FSXSE too).
            return catalog.Aircraft.Where(a => !a.Hidden).Count(aircraft => aircraft.Variants.Any(variant =>
                (catalog.Resolve(variant) ?? variant).Games.Any(g => string.Equals((g.Game ?? string.Empty).Trim(), gameId, StringComparison.OrdinalIgnoreCase))));
        }

        // DCS, IL-2 Great Battles and Star Citizen read and write keyboard binds and modifiers (EngineDcsExport,
        // EngineIl2, EngineStarCitizen); the registry games say it themselves (IGameIntegration.KeyboardBinds).
        private static string KeyboardMark(string id, IGameIntegration game)
        {
            return game == null ? Yes : Mark(game.KeyboardBinds);
        }

        private static string ModifierMark(string id, IGameIntegration game)
        {
            return game == null ? Yes : Mark(game.ModifierBinds);
        }

        private static string Mark(GameFeature feature)
        {
            return feature == GameFeature.Yes ? Yes : feature == GameFeature.Partial ? Partial : No;
        }

        // ---- the hand-kept notes ------------------------------------------------------------------------------------

        public static string ReadNotes(string path, out string source)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                source = path;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            source = "built in";
            using (Stream stream = typeof(EngineGameSupport).Assembly.GetManifestResourceStream(NotesResource))
            {
                if (stream == null)
                    return null;
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        public static Report Build(string notesPath)
        {
            Report report = new Report();
            List<Row> code = CodeRows(report.Warnings);
            string source;
            string text;
            try
            {
                text = ReadNotes(notesPath, out source);
            }
            catch (Exception ex)
            {
                report.Errors.Add("The notes could not be read: " + ex.Message);
                report.Rows = code;
                return report;
            }
            report.NotesSource = source;
            if (text == null)
            {
                report.Errors.Add("The notes are missing (" + NotesResource + ").");
                report.Rows = code;
                return report;
            }
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(text) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                report.Errors.Add("The notes are not valid JSON: " + ex.Message);
                report.Rows = code;
                return report;
            }
            if (root == null)
            {
                report.Errors.Add("The notes must be a JSON object.");
                report.Rows = code;
                return report;
            }
            report.Updated = Str(root, "updated");
            foreach (Dictionary<string, object> item in Objects(root, "categories"))
                report.Categories.Add(new Category { Id = Str(item, "id"), Title = Str(item, "title"), Note = Str(item, "note") });

            Dictionary<string, Row> byId = code.ToDictionary(r => r.GameId, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Row> rows = new List<Row>();
            foreach (Dictionary<string, object> item in Objects(root, "games"))
            {
                string id = Str(item, "id");
                if (id.Length == 0)
                {
                    report.Errors.Add("A game in the notes has no id.");
                    continue;
                }
                if (!seen.Add(id))
                {
                    report.Errors.Add("The notes list " + id + " twice.");
                    continue;
                }
                Row row;
                bool documented = !byId.TryGetValue(id, out row);
                if (documented)
                {
                    if (!string.Equals(Str(item, "level"), "documented", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Errors.Add("The notes list " + id + ", which the engine does not know; a title JoyPro only documents needs \"level\": \"documented\".");
                        continue;
                    }
                    row = new Row { GameId = id, Level = "documented" };
                    foreach (Column column in Columns)
                        Set(row, column.Key, NotApplicable, string.Empty);
                }
                else if (Str(item, "level").Length > 0 && !string.Equals(Str(item, "level"), row.Level, StringComparison.OrdinalIgnoreCase))
                {
                    report.Errors.Add(id + ": the notes say level " + Str(item, "level") + ", the code says " + row.Level + " (IGameDeviceLimit).");
                }
                MergeNotes(row, item, documented, report);
                if (report.Categories.Count > 0 && !report.Categories.Any(c => c.Id == row.Category))
                    report.Errors.Add(id + ": unknown category \"" + row.Category + "\".");
                rows.Add(row);
            }
            foreach (Row row in code.Where(r => !seen.Contains(r.GameId)))
            {
                report.Errors.Add("The notes have no entry for " + row.GameId + " (" + row.Title + "): add it to docs/games/support-notes.json.");
                rows.Add(row);
            }
            report.Rows = rows;
            CheckPartialNotes(report);
            return report;
        }

        private static void MergeNotes(Row row, Dictionary<string, object> item, bool documented, Report report)
        {
            string title = Str(item, "title");
            if (title.Length > 0)
                row.Title = title;
            row.Category = Str(item, "category");
            row.Doc = Str(item, "doc");
            row.BindFile = Str(item, "bindFile");
            row.ListSource = Str(item, "listSource");
            row.ListSize = Str(item, "listSize");
            row.Devices = Str(item, "devices");
            row.AntiCheat = Str(item, "antiCheat");
            row.Open = Str(item, "open");
            Dictionary<string, object> test = Obj(item, "test");
            if (test != null)
            {
                row.Verification = Str(test, "level");
                row.TestResult = Str(test, "result");
                row.TestDate = Str(test, "date");
                row.TestNote = Str(test, "note");
            }
            if (row.Verification.Length == 0)
                row.Verification = documented ? "none" : "blind";
            string[] levels = { "e2e", "partial", "files", "blind", "none" };
            if (!levels.Contains(row.Verification))
                report.Errors.Add(row.GameId + ": unknown test level \"" + row.Verification + "\" (" + string.Join(", ", levels) + ").");
            string e2eMark = row.Verification == "e2e" ? Yes : row.Verification == "partial" ? Partial : documented ? NotApplicable : No;
            Set(row, "e2e", e2eMark, row.TestResult);
            if (row.Verification == "partial" && row.TestNote.Length > 0)
                row.Cells["e2e"].Note = row.TestNote;

            Dictionary<string, object> cells = Obj(item, "cells");
            if (cells == null)
                return;
            foreach (KeyValuePair<string, object> entry in cells)
            {
                Column column = Columns.FirstOrDefault(c => string.Equals(c.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> cellNote = entry.Value as Dictionary<string, object>;
                if (column == null || cellNote == null)
                {
                    report.Errors.Add(row.GameId + ": unknown cell \"" + entry.Key + "\" in the notes.");
                    continue;
                }
                Cell cell = row.Cells[column.Key];
                string mark = Str(cellNote, "mark");
                string note = Str(cellNote, "note");
                if (mark.Length > 0 && mark != cell.Mark)
                {
                    // Notes qualify what the code says, never claim more: ✓ -> ◐ ✗ —, ◐ -> ✗ —, ✗ -> —.
                    int from = Array.IndexOf(MarkRank, cell.Mark), to = Array.IndexOf(MarkRank, mark);
                    if (to < 0 || from < 0 || to <= from)
                        report.Errors.Add(row.GameId + "/" + column.Key + ": the notes may not turn " + cell.Mark + " into \"" + mark + "\" (only ✓ -> ◐ ✗ —, ◐ -> ✗ —, ✗ -> —; the code decides what is supported).");
                    else if (note.Length == 0)
                        report.Errors.Add(row.GameId + "/" + column.Key + ": a changed mark needs a note.");
                    else
                        cell.Mark = mark;
                }
                if (Str(cellNote, "detail").Length > 0)
                    cell.Detail = Str(cellNote, "detail");
                if (note.Length > 0)
                    cell.Note = note;
            }
        }

        private static readonly string[] MarkRank = { Yes, Partial, No, NotApplicable };

        // Every ◐ says why (a numbered note on the page).
        private static void CheckPartialNotes(Report report)
        {
            foreach (Row row in report.Rows)
            {
                foreach (Column column in Columns)
                {
                    Cell cell = row.Cells[column.Key];
                    if (cell.Mark == Partial && cell.Note.Length == 0)
                        report.Errors.Add(row.GameId + "/" + column.Key + ": ◐ needs a note in docs/games/support-notes.json (\"cells\": {\"" + column.Key + "\": {\"note\": \"...\"}}).");
                }
            }
        }

        // ---- the page -----------------------------------------------------------------------------------------------

        private sealed class Block
        {
            public string Kind;
            public string Text = string.Empty;
            public List<string[]> Table;
            public List<string> Items;
        }

        private static readonly string[] LevelOrder = { "native", "virtual", "xbox", "documented" };

        public static string LevelText(string level)
        {
            switch (level)
            {
                case "native": return "native";
                case "virtual": return "virtual vJoy";
                case "xbox": return "virtual Xbox pad";
                case "documented": return "documented only";
                default: return level;
            }
        }

        private static string VerificationText(Row row)
        {
            string result = row.TestResult.Length > 0 ? row.TestResult + (row.TestDate.Length > 0 ? " (" + row.TestDate + ")" : string.Empty) : row.TestDate;
            string text;
            switch (row.Verification)
            {
                case "e2e": text = "in-game e2e" + (result.Length > 0 ? ": " + result : string.Empty); break;
                case "partial": text = "in game, partial" + (result.Length > 0 ? ": " + result : string.Empty); break;
                case "files": text = "installed files only"; break;
                case "blind": text = "blind (not installed)"; break;
                default: text = "—"; break;
            }
            // A partial run's note is the matrix footnote; it is repeated here so the row reads on its own.
            if (row.TestNote.Length > 0)
                text += "; " + row.TestNote;
            return text;
        }

        private static List<Block> Page(Report report)
        {
            List<Block> blocks = new List<Block>();
            List<string> footnotes = new List<string>();
            Func<string, string> footnote = note =>
            {
                int index = footnotes.IndexOf(note);
                if (index < 0)
                {
                    footnotes.Add(note);
                    index = footnotes.Count - 1;
                }
                return "<sup>" + (index + 1).ToString(CultureInfo.InvariantCulture) + "</sup>";
            };
            List<Category> categories = report.Categories.Count > 0
                ? report.Categories
                : report.Rows.Select(r => r.Category).Distinct().Select(c => new Category { Id = c, Title = c.Length > 0 ? c : "Other" }).ToList();
            Func<Category, List<Row>> rowsOf = c => report.Rows.Where(r => r.Category == c.Id).ToList();

            int inEngine = report.Rows.Count(r => r.InEngine);
            blocks.Add(new Block { Kind = "h1", Text = "Game support" });
            blocks.Add(new Block { Kind = "comment", Text = "Generated by `engine games support --markdown` (tools/game_support.ps1) from the engine's code and docs/games/support-notes.json. Do not edit this file: change the notes or the code and run the tool." });
            blocks.Add(new Block { Kind = "p", Text = "Last updated: " + (report.Updated.Length > 0 ? report.Updated : "?") + ". How far JoyPro supports each game: " +
                report.Rows.Count.ToString(CultureInfo.InvariantCulture) + " titles, " + inEngine.ToString(CultureInfo.InvariantCulture) + " of them handled by JoyPro (every version separately), " +
                (report.Rows.Count - inEngine).ToString(CultureInfo.InvariantCulture) + " only researched and documented. " +
                "The marks come from the code (what each game integration implements); verification results, footnotes and the facts per title come from the hand-kept " +
                "[support-notes.json](support-notes.json). In the app: Start page, `Supported games` next to `Your games`." });

            blocks.Add(new Block { Kind = "h2", Text = "Legend" });
            blocks.Add(new Block
            {
                Kind = "ul",
                Items = new List<string>
                {
                    Yes + " supported; " + Partial + " partial or experimental (see the numbered note); " + No + " not supported; " + NotApplicable + " not applicable (the game has no such thing, or JoyPro does not handle the title).",
                    "Support level: **native** = JoyPro reads and writes the game's own bind files for your real devices; **virtual vJoy** = the game reads too few joysticks, JoyPro feeds it one virtual joystick (vJoy, devices hidden with HidHide); **virtual Xbox pad** = the game reads no joystick, JoyPro feeds it a virtual Xbox 360 pad (ViGEmBus); **documented only** = researched, see the linked page, JoyPro has no integration.",
                    "Numbers: Setup wizard / Auto Assign = rows in JoyPro's function table of the game; Cockpits = cockpits of the game's aircraft in the cockpit database; In-game test = passed / total steps of the best run.",
                    "In-game test: " + Yes + " a green in-game end-to-end run (JoyPro's test kit drives the real game); " + Partial + " a run in game that was not fully green or covered only the menus; " + No + " checked against installed game files only, or implemented blind from public files (the details say which).",
                    "Every export backs up the files first, refuses to write while the game runs and writes only cataloged actions; see [README.md](README.md) and [../export.md](../export.md).",
                    "Anti-cheat: JoyPro reads no game memory and injects nothing; it writes config files (or registry values) while the game is closed. vJoy, ViGEmBus and HidHide are signed drivers that anti-cheat normally accepts, but a game may refuse virtual devices."
                }.Concat(Columns.Select(c => "**" + c.Header + "**: " + c.Meaning + ".")).ToList()
            });

            blocks.Add(new Block { Kind = "h2", Text = "Summary" });
            List<string[]> summary = new List<string[]> { new[] { "Category", "Titles", "Native", "Virtual vJoy", "Virtual Xbox pad", "Documented only", "In-game test " + Yes, "In-game test " + Partial } };
            foreach (Category category in categories)
            {
                List<Row> rows = rowsOf(category);
                summary.Add(new[]
                {
                    category.Title, N(rows.Count), N(rows.Count(r => r.Level == "native")), N(rows.Count(r => r.Level == "virtual")), N(rows.Count(r => r.Level == "xbox")),
                    N(rows.Count(r => r.Level == "documented")), N(rows.Count(r => r.Verification == "e2e")), N(rows.Count(r => r.Verification == "partial"))
                });
            }
            summary.Add(new[]
            {
                "**All**", N(report.Rows.Count), N(report.Rows.Count(r => r.Level == "native")), N(report.Rows.Count(r => r.Level == "virtual")), N(report.Rows.Count(r => r.Level == "xbox")),
                N(report.Rows.Count(r => r.Level == "documented")), N(report.Rows.Count(r => r.Verification == "e2e")), N(report.Rows.Count(r => r.Verification == "partial"))
            });
            blocks.Add(new Block { Kind = "table", Table = summary });

            blocks.Add(new Block { Kind = "h2", Text = "Feature matrix" });
            foreach (Category category in categories)
            {
                List<Row> rows = rowsOf(category);
                if (rows.Count == 0)
                    continue;
                blocks.Add(new Block { Kind = "h3", Text = category.Title });
                if (category.Note.Length > 0)
                    blocks.Add(new Block { Kind = "p", Text = category.Note });
                List<string[]> table = new List<string[]> { new[] { "Game", "Support" }.Concat(Columns.Select(c => c.Header)).ToArray() };
                foreach (Row row in rows)
                {
                    List<string> cells = new List<string> { TitleLink(row), LevelText(row.Level) };
                    foreach (Column column in Columns)
                    {
                        Cell cell = row.Cells[column.Key];
                        string text = cell.Mark;
                        if (cell.Detail.Length > 0 && (column.Key == "wizard" || column.Key == "cockpits" || column.Key == "e2e"))
                            text += " " + cell.Detail;
                        if (cell.Note.Length > 0)
                            text += footnote(cell.Note);
                        cells.Add(text);
                    }
                    table.Add(cells.ToArray());
                }
                blocks.Add(new Block { Kind = "table", Table = table });
            }
            blocks.Add(new Block { Kind = "h3", Text = "Notes on the matrix" });
            blocks.Add(new Block { Kind = "ol", Items = footnotes });

            blocks.Add(new Block { Kind = "h2", Text = "Details per title" });
            blocks.Add(new Block { Kind = "p", Text = "Bind file and command list: where JoyPro reads and writes on your PC and what it builds the game's command list from (list size measured on the development PC, " +
                "see [README.md](README.md#command-lists-on-a-fresh-install)). Device limits: what the game reads of your devices; the input limits with their confidence " +
                "(verified = from the game's files or JoyPro's own code, documented = the game's docs, reported = players) warn in the virtual joystick planner ([../virtual-joystick.md](../virtual-joystick.md))." });
            foreach (Category category in categories)
            {
                List<Row> rows = rowsOf(category);
                if (rows.Count == 0)
                    continue;
                blocks.Add(new Block { Kind = "h3", Text = category.Title });
                List<string[]> table = new List<string[]> { new[] { "Game", "Support", "Command line", "Bind file and format", "Command list on your PC", "Export clean / nuke, axis model, settings", "Device limits", "Anti-cheat", "Verification", "Open points" } };
                foreach (Row row in rows)
                {
                    table.Add(new[]
                    {
                        TitleLink(row),
                        LevelText(row.Level) + (row.Experimental ? ", experimental" : string.Empty),
                        row.InEngine ? "`" + row.Command + "`" : NotApplicable,
                        Dash(row.BindFile),
                        Dash(Join("; ", row.ListSource, row.ListSize.Length > 0 ? "size: " + row.ListSize : string.Empty)),
                        row.InEngine ? Join("; ", row.ExclusiveFlightSim ? "clean / nuke: whole devices" : "clean / nuke: flight actions only",
                            row.AxisModel.Length > 0 ? "axis model `" + row.AxisModel + "`" : "no axis model", Settings(row)) : NotApplicable,
                        Dash(Join("; ", DeviceText(row), row.Devices)),
                        Dash(row.AntiCheat),
                        VerificationText(row),
                        Dash(row.Open)
                    });
                }
                blocks.Add(new Block { Kind = "table", Table = table });
            }

            blocks.Add(new Block { Kind = "h2", Text = "Keeping this page true" });
            blocks.Add(new Block
            {
                Kind = "ul",
                Items = new List<string>
                {
                    "`powershell -ExecutionPolicy Bypass -File tools/game_support.ps1` writes this page; `-CheckOnly` fails when it is out of date (the engine smoke test runs it).",
                    "`engine games support` prints the same facts as JSON (`--markdown`, `--html` for the page; `--notes <file>` reads other notes than the ones built into engine.exe).",
                    "The check also fails when a registered game has no entry in the notes, a note names an unknown game or cell, or a note claims more than the code (notes may only turn " + Yes + " into " + Partial + ", " + No + " or " + NotApplicable + ", each with a reason).",
                    "After an in-game test run: update `test` of the game in the notes (level, result, date), then run the tool."
                }
            });
            return blocks;
        }

        private static string N(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Dash(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? NotApplicable : text;
        }

        private static string Join(string separator, params string[] parts)
        {
            return string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string TitleLink(Row row)
        {
            return row.Doc.Length > 0 ? "[" + row.Title + "](" + row.Doc + ")" : row.Title;
        }

        private static string Settings(Row row)
        {
            List<string> parts = new List<string>();
            if (row.Settings.Count > 0)
                parts.Add(N(row.Settings.Count) + (row.Settings.Count == 1 ? " game setting" : " game settings"));
            if (row.FfbSettings.Count > 0)
                parts.Add(N(row.FfbSettings.Count) + (row.FfbSettings.Count == 1 ? " force feedback setting" : " force feedback settings"));
            return string.Join(", ", parts);
        }

        private static string DeviceText(Row row)
        {
            List<string> parts = new List<string>();
            if (row.MaxJoysticks == 0)
                parts.Add("no joystick (XInput only)");
            else if (row.MaxJoysticks > 0)
                parts.Add(N(row.MaxJoysticks) + (row.MaxJoysticks == 1 ? " joystick" : " joysticks") + " at once");
            if (row.InputLimit.Length > 0 && row.MaxJoysticks != 0)
                parts.Add(row.InputLimit);
            return string.Join("; ", parts);
        }

        public static string ToMarkdown(Report report)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Block block in Page(report))
            {
                switch (block.Kind)
                {
                    case "h1": sb.Append("# ").Append(block.Text).Append("\n\n"); break;
                    case "h2": sb.Append("## ").Append(block.Text).Append("\n\n"); break;
                    case "h3": sb.Append("### ").Append(block.Text).Append("\n\n"); break;
                    case "comment": sb.Append("<!-- ").Append(block.Text).Append(" -->\n\n"); break;
                    case "p": sb.Append(block.Text).Append("\n\n"); break;
                    case "ul":
                        foreach (string item in block.Items)
                            sb.Append("- ").Append(item).Append('\n');
                        sb.Append('\n');
                        break;
                    case "ol":
                        for (int i = 0; i < block.Items.Count; i++)
                            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(block.Items[i]).Append('\n');
                        sb.Append('\n');
                        break;
                    case "table":
                        for (int r = 0; r < block.Table.Count; r++)
                        {
                            sb.Append("| ").Append(string.Join(" | ", block.Table[r].Select(MdCell))).Append(" |\n");
                            if (r == 0)
                                sb.Append('|').Append(string.Join("|", block.Table[r].Select(c => "---"))).Append("|\n");
                        }
                        sb.Append('\n');
                        break;
                }
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        private static string MdCell(string text)
        {
            return (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        }

        public static string ToHtml(Report report)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append("<title>JoyPro2 - Supported games</title><style>")
              .Append(":root{color-scheme:light dark;--bg:#ffffff;--fg:#1d2330;--muted:#5d6675;--line:#d6dbe3;--head:#eef1f6;--accent:#2f6fd0}")
              .Append("@media (prefers-color-scheme:dark){:root{--bg:#161a21;--fg:#e4e8ef;--muted:#9aa3b2;--line:#323a47;--head:#212833;--accent:#6ea3ff}}")
              .Append("body{background:var(--bg);color:var(--fg);font:14px/1.45 'Segoe UI',system-ui,sans-serif;margin:0 auto;padding:16px 20px;max-width:1800px}")
              .Append("h1{font-size:26px}h2{font-size:20px;margin-top:28px}h3{font-size:16px;margin-top:20px}")
              .Append(".scroll{overflow-x:auto}table{border-collapse:collapse;margin:8px 0 12px}th,td{border:1px solid var(--line);padding:4px 7px;vertical-align:top;text-align:left}")
              .Append("th{background:var(--head);position:sticky;top:0}td{min-width:2.2em}code{font-family:Consolas,monospace;font-size:12.5px}sup{color:var(--accent)}p,li{max-width:120ch}")
              .Append("</style></head><body>\n");
            foreach (Block block in Page(report))
            {
                switch (block.Kind)
                {
                    case "h1": sb.Append("<h1>").Append(Inline(block.Text)).Append("</h1>\n"); break;
                    case "h2": sb.Append("<h2>").Append(Inline(block.Text)).Append("</h2>\n"); break;
                    case "h3": sb.Append("<h3>").Append(Inline(block.Text)).Append("</h3>\n"); break;
                    case "comment": break;
                    case "p": sb.Append("<p>").Append(Inline(block.Text)).Append("</p>\n"); break;
                    case "ul":
                    case "ol":
                        sb.Append('<').Append(block.Kind).Append('>');
                        foreach (string item in block.Items)
                            sb.Append("<li>").Append(Inline(item)).Append("</li>");
                        sb.Append("</").Append(block.Kind).Append(">\n");
                        break;
                    case "table":
                        sb.Append("<div class=\"scroll\"><table>");
                        for (int r = 0; r < block.Table.Count; r++)
                        {
                            string tag = r == 0 ? "th" : "td";
                            sb.Append("<tr>");
                            foreach (string cell in block.Table[r])
                                sb.Append('<').Append(tag).Append('>').Append(Inline(cell)).Append("</").Append(tag).Append('>');
                            sb.Append("</tr>");
                        }
                        sb.Append("</table></div>\n");
                        break;
                }
            }
            sb.Append("</body></html>\n");
            return sb.ToString();
        }

        // The page's own Markdown inline marks as HTML: `code`, **bold**, [text](link) (as text: the linked pages are not
        // in an installed copy) and the footnote numbers.
        private static string Inline(string text)
        {
            string html = WebUtility.HtmlEncode(text ?? string.Empty);
            html = System.Text.RegularExpressions.Regex.Replace(html, "&lt;sup&gt;(\\d+)&lt;/sup&gt;", "<sup>$1</sup>");
            html = System.Text.RegularExpressions.Regex.Replace(html, "`([^`]+)`", "<code>$1</code>");
            html = System.Text.RegularExpressions.Regex.Replace(html, "\\*\\*([^*]+)\\*\\*", "<b>$1</b>");
            html = System.Text.RegularExpressions.Regex.Replace(html, "\\[([^\\]]+)\\]\\([^)]+\\)", "$1");
            return html;
        }

        public static string ToText(Report report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Game support: " + N(report.Rows.Count) + " titles (" + N(report.Rows.Count(r => r.InEngine)) + " in the engine), notes " + report.NotesSource + ", updated " + report.Updated);
            foreach (Row row in report.Rows)
            {
                sb.AppendLine("  - " + row.GameId + " | " + row.Title + " | " + row.Category + " | " + LevelText(row.Level));
                sb.AppendLine("      " + string.Join(" ", Columns.Select(c => c.Key + "=" + row.Cells[c.Key].Mark + (row.Cells[c.Key].Detail.Length > 0 ? "(" + row.Cells[c.Key].Detail + ")" : string.Empty))));
            }
            foreach (string warning in report.Warnings)
                sb.AppendLine("Warning: " + warning);
            foreach (string error in report.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }

        // engine games support [--markdown | --html] [--notes <file>]; exit 1 when the notes do not match the code.
        public static int Run(List<string> args, bool textOutput, out string payload)
        {
            payload = string.Empty;
            bool markdown = args.RemoveAll(a => string.Equals(a, "--markdown", StringComparison.OrdinalIgnoreCase)) > 0;
            bool html = args.RemoveAll(a => string.Equals(a, "--html", StringComparison.OrdinalIgnoreCase)) > 0;
            bool json = args.RemoveAll(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)) > 0;
            string notes = string.Empty;
            int at = args.FindIndex(a => string.Equals(a, "--notes", StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                if (at + 1 >= args.Count)
                    throw new ArgumentException("--notes needs a file.");
                notes = args[at + 1];
                args.RemoveRange(at, 2);
            }
            if (args.Count > 0)
                throw new ArgumentException("Unknown games support argument(s): " + string.Join(" ", args));
            if ((markdown ? 1 : 0) + (html ? 1 : 0) + (json ? 1 : 0) > 1)
                throw new ArgumentException("Use one of --markdown, --html, --json.");
            Report report = Build(notes);
            payload = markdown ? ToMarkdown(report) : html ? ToHtml(report) : textOutput ? ToText(report) : EngineSerialization.ToJson(report);
            if (report.Errors.Count > 0)
            {
                foreach (string error in report.Errors)
                    Console.Error.WriteLine("games support: " + error);
                return 1;
            }
            return 0;
        }

        private static string Str(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null)
                return string.Empty;
            List<object> list = value as List<object>;
            if (list != null)
                return string.Join(" ", list.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))).Trim();
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || !(value is List<object>))
                return Enumerable.Empty<Dictionary<string, object>>();
            return ((List<object>)value).OfType<Dictionary<string, object>>();
        }
    }
}
