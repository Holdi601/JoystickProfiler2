using System;

namespace JoyPro.Engine
{
    // The engine's access to JoyPro's own action names and explanations (ActionTexts, DB\Functions\ActionTexts).
    internal static class EngineActionTexts
    {
        public static string Folder()
        {
            return ActionTexts.FolderUnder(GameDataPaths.DbFolder("Functions"));
        }

        public static ActionTexts.Entry Find(string game, string id)
        {
            return ActionTexts.Find(Folder(), game, id);
        }

        public static string Title(string game, string id, string title)
        {
            return ActionTexts.BestTitle(Folder(), game, id, title);
        }

        // The section of the game's own menu where the table has one: for IL-2 (Great Battles and Korea) it replaces
        // the guessed category, elsewhere it only fills an empty one.
        public static string Category(string game, string id, string category)
        {
            ActionTexts.Entry entry = Find(game, id);
            if (entry == null || entry.Category.Length == 0)
                return category ?? string.Empty;
            bool il2 = ActionTexts.NameWins(game) || string.Equals(game, "IL2Game", StringComparison.OrdinalIgnoreCase);
            return il2 || string.IsNullOrWhiteSpace(category) ? entry.Category : category;
        }
    }
}
