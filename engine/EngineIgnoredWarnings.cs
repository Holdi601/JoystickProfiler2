using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine
{
    // The check's ignored warnings (the app's "Ignore" on a card; Pr0file.KeptDuplicates): one entry per warning with
    // what the user saw when ignoring it:
    //   "game|id|plane|<relation>\t<relation>"            the same command on several buttons (EngineRelationDuplicates)
    //   "input|game:plane§device§input...|<relation>\t..." one button does two things (EngineCheckConflicts)
    //   "active|game|plane|relation|<id>\t<id>"           one button runs two commands (EngineCheckConflicts)
    // A warning stays hidden while its relations (actions) are those of the entry or fewer of them. When one more joins,
    // the warning shows again and says what joined ("shown again"); the old entry stays only while it says that: ignoring
    // the warning again replaces it, a fix of the check drops it once nothing of that key is left (Prune). A listing never
    // changes the list.
    internal static class EngineIgnoredWarnings
    {
        internal const string InputPrefix = "input|";
        internal const string ActionPrefix = "active|";

        internal sealed class Entry
        {
            public string Text = string.Empty;
            // "", InputPrefix or ActionPrefix.
            public string Kind = string.Empty;
            // The warning's key (duplicates: game|id|plane; input conflicts: game:plane§device§input...; action conflicts:
            // game|plane|relation).
            public string Key = string.Empty;
            public HashSet<string> Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal static Entry Parse(string text)
        {
            string t = text ?? string.Empty;
            string kind = t.StartsWith(InputPrefix, StringComparison.OrdinalIgnoreCase) ? InputPrefix
                : t.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase) ? ActionPrefix : string.Empty;
            string rest = t.Substring(kind.Length);
            int bar = rest.LastIndexOf('|');
            Entry entry = new Entry { Text = t, Kind = kind, Key = bar < 0 ? rest : rest.Substring(0, bar) };
            if (bar >= 0)
            {
                foreach (string member in rest.Substring(bar + 1).Split('\t'))
                {
                    if (member.Length > 0)
                        entry.Set.Add(member);
                }
            }
            return entry;
        }

        internal static string Text(string kind, string key, IEnumerable<string> members)
        {
            return kind + key + "|" + string.Join("\t", members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        }

        // The entries by kind and key, read once per listing.
        internal sealed class Index
        {
            private readonly Dictionary<string, List<Entry>> byKey = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

            public Index(IEnumerable<string> entries)
            {
                foreach (string text in entries ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    Entry entry = Parse(text);
                    List<Entry> list;
                    if (!byKey.TryGetValue(entry.Kind + entry.Key, out list))
                        byKey[entry.Kind + entry.Key] = list = new List<Entry>();
                    list.Add(entry);
                }
            }

            // Hidden: an entry of the key has every one of the warning's relations (actions) now.
            public bool Covers(string kind, string key, ICollection<string> now)
            {
                List<Entry> list;
                return byKey.TryGetValue(kind + key, out list) && list.Any(e => now.All(e.Set.Contains));
            }

            // The entry of a warning that shows again (one more relation or action joined since it was ignored); null: the
            // warning was never ignored, or is hidden.
            public Entry ShownAgain(string kind, string key, ICollection<string> now)
            {
                List<Entry> list;
                if (!byKey.TryGetValue(kind + key, out list) || list.Any(e => now.All(e.Set.Contains)))
                    return null;
                return list.OrderByDescending(e => e.Set.Count(now.Contains)).ThenBy(e => e.Text, StringComparer.OrdinalIgnoreCase).First();
            }
        }

        internal static Index Read()
        {
            return new Index(InternalDataManagement.KeptDuplicates ?? new List<string>());
        }

        // Ignores a warning as it is now: the entry of what the user sees replaces older ones of the same key.
        internal static bool Add(string kind, string key, IEnumerable<string> members)
        {
            if (InternalDataManagement.KeptDuplicates == null)
                InternalDataManagement.KeptDuplicates = new List<string>();
            string text = Text(kind, key, members);
            List<string> list = InternalDataManagement.KeptDuplicates;
            bool had = list.Contains(text, StringComparer.OrdinalIgnoreCase);
            list.RemoveAll(e => !string.Equals(e, text, StringComparison.OrdinalIgnoreCase) && SameWarning(Parse(e), kind, key));
            if (had)
                return false;
            list.Add(text);
            return true;
        }

        private static bool SameWarning(Entry entry, string kind, string key)
        {
            return string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase);
        }

        // "Show again": forgets the entries of these warnings' keys (kinds: the kinds looked at). Returns how many went.
        internal static int Forget(IEnumerable<string> kinds, ICollection<string> keys)
        {
            if (InternalDataManagement.KeptDuplicates == null)
                return 0;
            HashSet<string> wanted = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
            return InternalDataManagement.KeptDuplicates.RemoveAll(text =>
            {
                Entry entry = Parse(text);
                return wanted.Contains(entry.Kind) && (keys == null || keys.Contains(entry.Key));
            });
        }

        // After a fix of the check: the entries whose warning is not there at all any more go (the command is on one
        // button only, the button runs one command). An entry whose warning shows again stays: it tells what joined.
        internal static int Prune()
        {
            List<string> list = InternalDataManagement.KeptDuplicates;
            if (list == null || list.Count == 0)
                return 0;
            List<Entry> entries = list.Select(Parse).ToList();
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entries.Any(e => e.Kind.Length == 0))
            {
                foreach (string key in EngineRelationDuplicates.FindKeys(includeKept: true))
                    present.Add(key);
            }
            if (entries.Any(e => e.Kind == InputPrefix))
            {
                foreach (EngineInputConflict conflict in EngineCheckConflicts.InputConflicts(includeKept: true, withTitles: false))
                    present.Add(InputPrefix + conflict.Key);
            }
            if (entries.Any(e => e.Kind == ActionPrefix))
            {
                foreach (EngineActionConflict conflict in EngineCheckConflicts.ActionConflicts(includeKept: true, withTitles: false))
                    present.Add(ActionPrefix + conflict.Key);
            }
            return list.RemoveAll(text =>
            {
                Entry entry = Parse(text);
                return !present.Contains(entry.Kind + entry.Key);
            });
        }
    }
}
