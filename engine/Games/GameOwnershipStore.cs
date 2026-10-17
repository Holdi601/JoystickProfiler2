using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // "<config file>.joypro.json" next to a game file: per action, the inputs JoyPro wrote or took over on
    // import. JoyPro metadata never goes into the game file itself.
    internal static class GameOwnershipStore
    {
        public const string Suffix = ".joypro.json";

        private sealed class Document
        {
            public string Game { get; set; }
            public string UpdatedUtc { get; set; }
            public Dictionary<string, List<string>> Owned { get; set; }
        }

        public static Dictionary<string, HashSet<string>> Load(string configPath, List<string> warnings)
        {
            Dictionary<string, HashSet<string>> owned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string path = configPath + Suffix;
            if (!File.Exists(path))
                return owned;

            try
            {
                Document document = EngineSerialization.FromJson<Document>(File.ReadAllText(path, Encoding.UTF8));
                if (document != null && document.Owned != null)
                {
                    foreach (KeyValuePair<string, List<string>> entry in document.Owned)
                        owned[entry.Key] = new HashSet<string>(entry.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Ignored unreadable JoyPro ownership file '" + path + "': " + ex.Message);
            }

            return owned;
        }

        public static void Save(string configPath, string game, Dictionary<string, HashSet<string>> owned)
        {
            Document document = new Document
            {
                Game = game,
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                Owned = owned
                    .Where(e => e.Value != null && e.Value.Count > 0)
                    .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(e => e.Key, e => e.Value.OrderBy(v => v, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase)
            };
            // Unchanged ownership: no rewrite (a new timestamp alone would make every export change a file).
            Dictionary<string, HashSet<string>> stored = Load(configPath, null);
            if (stored.Count == document.Owned.Count &&
                document.Owned.All(e => stored.ContainsKey(e.Key) && stored[e.Key].SetEquals(e.Value)) &&
                File.Exists(configPath + Suffix))
                return;
            GameBackups.Protect(configPath + Suffix, null);
            File.WriteAllText(configPath + Suffix, EngineSerialization.ToJson(document), new UTF8Encoding(false));
        }

        public static void Add(Dictionary<string, HashSet<string>> owned, string action, string token)
        {
            HashSet<string> set;
            if (!owned.TryGetValue(action, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owned[action] = set;
            }
            set.Add(token);
        }

        public static bool Contains(Dictionary<string, HashSet<string>> owned, string action, string token)
        {
            HashSet<string> set;
            return owned.TryGetValue(action, out set) && set.Contains(token);
        }
    }
}
