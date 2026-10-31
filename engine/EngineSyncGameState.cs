using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // engine profile game-state --game <id> [--path <config>]: has this profile imported from or exported to the game,
    // and does the game have binds of its own. The GUI asks once before the first export to a game the profile never
    // imported from ("Export replaces the binds in <game> with your profile ... Import first?").
    public sealed class EngineProfileGameState
    {
        public string Game { get; set; }
        public string ConfigPath { get; set; }
        public bool Imported { get; set; }
        public bool Exported { get; set; }
        // Exported was concluded from the sync record of an export made before 2.5.0 (most of the profile's binds are
        // the ones that export wrote).
        public bool ExportedByRecord { get; set; }
        public bool GameHasBinds { get; set; }
        public int GameBindCount { get; set; }
        public List<string> Warnings { get; set; }

        public EngineProfileGameState()
        {
            Game = ConfigPath = string.Empty;
            Warnings = new List<string>();
        }
    }

    internal static partial class EngineSync
    {
        // Set when an import or export made a game one the profile is synced with (Pr0file.SyncedGames): the run saves
        // the profile then.
        public static bool ProfileSyncedGamesChanged;

        // "import|DCS", "export|IL2Game" in Pr0file.SyncedGames.
        private static void MarkProfileSynced(string gameId, string reason)
        {
            if (string.IsNullOrWhiteSpace(gameId) || InternalDataManagement.AllRelations == null)
                return;
            if (InternalDataManagement.SyncedGames == null)
                InternalDataManagement.SyncedGames = new List<string>();
            string entry = reason + "|" + gameId.Trim();
            if (InternalDataManagement.SyncedGames.Contains(entry, StringComparer.OrdinalIgnoreCase))
                return;
            InternalDataManagement.SyncedGames.Add(entry);
            ProfileSyncedGamesChanged = true;
        }

        // An import of DCS, IL-2 or Star Citizen (their commands record no sync state of their own).
        public static void MarkImported(string gameName)
        {
            SyncGame game;
            MarkProfileSynced(TryResolveGame(gameName, out game) ? game.Id : gameName, "import");
        }

        private static bool ProfileSynced(string gameId, string reason)
        {
            return (InternalDataManagement.SyncedGames ?? new List<string>()).Contains(reason + "|" + gameId, StringComparer.OrdinalIgnoreCase);
        }

        public static EngineProfileGameState GameState(string gameName, string explicitPath)
        {
            EngineProfileGameState state = new EngineProfileGameState();
            state.Game = gameName ?? string.Empty;
            SyncGame game;
            if (!TryResolveGame(gameName, out game))
            {
                state.Warnings.Add("Unknown game: " + gameName);
                return state;
            }
            state.Game = game.Id;
            state.Imported = ProfileSynced(game.Id, "import");
            state.Exported = ProfileSynced(game.Id, "export");

            List<string> warnings = new List<string>();
            SyncLocation location = ResolveLocation(game, explicitPath, warnings);
            state.Warnings.AddRange(warnings);
            if (!location.Exists)
                return state;
            state.ConfigPath = location.ConfigPath;

            // A profile that exported before 2.5.0 has no SyncedGames entry; the sync record of that export still says
            // which binds it wrote. Most of the profile's binds being those means this profile exported there.
            if (!state.Imported && !state.Exported)
            {
                SyncRecord record = FindRecord(game.Id, location.ConfigPath);
                if (record != null && record.ProfileKeys != null && record.ProfileKeys.Count > 0)
                {
                    HashSet<string> recorded = new HashSet<string>(record.ProfileKeys, StringComparer.Ordinal);
                    List<string> mine = ProfileKeysOf(game);
                    if (mine.Count > 0 && mine.Count(recorded.Contains) * 2 >= mine.Count)
                    {
                        state.Exported = true;
                        state.ExportedByRecord = true;
                    }
                }
            }
            if (state.Imported || state.Exported)
                return state;

            List<string> readWarnings = new List<string>();
            List<string> readErrors = new List<string>();
            Dictionary<string, Relation> scratch;
            List<SyncEntry> entries = ReadCurrent(game, location, null, readWarnings, readErrors, out scratch);
            state.GameBindCount = readErrors.Count == 0 ? entries.Count : 0;
            state.GameHasBinds = state.GameBindCount > 0;
            state.Warnings.AddRange(readErrors);
            return state;
        }

        public static string GameStateText(EngineProfileGameState state)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Game: " + state.Game);
            sb.AppendLine("Config: " + state.ConfigPath);
            sb.AppendLine("Imported by this profile: " + state.Imported);
            sb.AppendLine("Exported by this profile: " + state.Exported + (state.ExportedByRecord ? " (sync record of an earlier export)" : string.Empty));
            sb.AppendLine("Game has binds of its own: " + state.GameHasBinds + " (" + state.GameBindCount.ToString(CultureInfo.InvariantCulture) + ")");
            foreach (string warning in state.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }
}
