using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // DCS commands found in the user's own bind files (Saved Games\<variant>\Config\Input\<aircraft>\...\*.diff.lua) that
    // the aircraft's command list (DB\DCS\<aircraft>.html) does not have: added by a mod after the list was built, or by
    // a DCS update. A .diff.lua names every command it binds by its exact id and DCS's name, so JoyPro knows them instead
    // of calling them "not in its list" (never guessed: only ids and names exactly as in the files).
    //
    // Kept per user in <data>\GameData\DcsCommandsFromYourFiles.tsv (outside DB\DCS, so the load correction's fingerprint
    // does not change with it). Every DCS command list read (InternalDataManagement.ParseDcsHtmlInputs) gets these
    // commands of its aircraft added, category "Found in your DCS files". A command recorded since the last refresh is
    // "new": the update check suggests rebuilding the list (db check-updates). A refresh removes the commands the new
    // list has; the others become "kept" (still known, no more rebuild hints), and so does "Keep DB".
    internal static class DcsFoundCommands
    {
        public const string FileName = "DcsCommandsFromYourFiles.tsv";
        public const string Category = "Found in your DCS files";

        internal sealed class Entry
        {
            public string Plane = string.Empty;
            public string Id = string.Empty;
            public string Title = string.Empty;
            public bool IsAxis;
            public string State = "new";
            public string Source = string.Empty;
        }

        private static readonly object Sync = new object();
        private static List<Entry> cached;
        private static string cachedStamp = string.Empty;

        public static string FilePath()
        {
            return Path.Combine(GameDataPaths.UserRoot(), FileName);
        }

        public static List<Entry> Load()
        {
            lock (Sync)
            {
                string path = FilePath();
                string stamp = string.Empty;
                try
                {
                    FileInfo info = new FileInfo(path);
                    stamp = info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "none";
                }
                catch (IOException)
                {
                    stamp = "error";
                }
                if (cached != null && stamp == cachedStamp)
                    return cached;
                List<Entry> entries = new List<Entry>();
                try
                {
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                        {
                            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                                continue;
                            string[] parts = line.Split('\t');
                            if (parts.Length < 5 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                                continue;
                            entries.Add(new Entry
                            {
                                Plane = parts[0].Trim(),
                                Id = parts[1].Trim(),
                                Title = parts[2],
                                IsAxis = parts[3] == "axis",
                                State = parts[4] == "kept" ? "kept" : "new",
                                Source = parts.Length > 5 ? parts[5] : string.Empty
                            });
                        }
                    }
                }
                catch (IOException)
                {
                }
                cached = entries;
                cachedStamp = stamp;
                return entries;
            }
        }

        private static void Save(List<Entry> entries)
        {
            lock (Sync)
            {
                string path = FilePath();
                if (entries.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    List<string> lines = new List<string>
                    {
                        "# DCS commands found in your bind files that the aircraft's command list lacks (JoyPro; see db check-updates).",
                        "# aircraft\tid\tname\tkey|axis\tnew|kept\tfile"
                    };
                    lines.AddRange(entries.OrderBy(e => e.Plane, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal)
                        .Select(e => string.Join("\t", e.Plane, e.Id, Clean(e.Title), e.IsAxis ? "axis" : "key", e.State, Clean(e.Source))));
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                }
                cached = null;
                cachedStamp = string.Empty;
            }
            InternalDataManagement.ForgetDcsCommandLists();
        }

        private static string Clean(string text)
        {
            return (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // The found commands of one aircraft whose command list is filePath, when that list is JoyPro's DCS list (not a
        // refresh's temporary export or a test's own folder).
        public static List<Entry> ForList(string filePath, string plane)
        {
            List<Entry> all = Load();
            if (all.Count == 0 || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(plane))
                return new List<Entry>();
            try
            {
                string folder = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? string.Empty).TrimEnd('\\');
                string dcs = Path.GetFullPath(GameDataPaths.DbFolder("DCS")).TrimEnd('\\');
                if (!string.Equals(folder, dcs, StringComparison.OrdinalIgnoreCase))
                    return new List<Entry>();
            }
            catch (Exception)
            {
                return new List<Entry>();
            }
            return all.Where(e => string.Equals(e.Plane, plane.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Records the commands of the user's bind files that the command lists of their aircraft lack: (aircraft, id,
        // name, axis, file). Only aircraft that have a command list count (without one everything would be missing).
        // Returns the newly recorded ones.
        public static List<Entry> Record(IEnumerable<Entry> found)
        {
            List<Entry> added = new List<Entry>();
            if (found == null)
                return added;
            List<Entry> entries = Load().Select(Copy).ToList();
            HashSet<string> known = new HashSet<string>(entries.Select(e => Key(e.Plane, e.Id)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> listIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry candidate in found)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Plane) || string.IsNullOrWhiteSpace(candidate.Id) || !LooksLikeDcsId(candidate.Id))
                    continue;
                string plane = candidate.Plane.Trim();
                string id = candidate.Id.Trim();
                if (known.Contains(Key(plane, id)))
                    continue;
                HashSet<string> ids;
                if (!listIds.TryGetValue(plane, out ids))
                {
                    string list = GameDataPaths.DbFile("DCS", plane + ".html");
                    ids = File.Exists(list) ? InternalDataManagement.DcsListIds(list) : null;
                    listIds[plane] = ids;
                }
                if (ids == null || ids.Count == 0 || ids.Contains(id))
                    continue;
                Entry entry = new Entry
                {
                    Plane = plane,
                    Id = id,
                    Title = string.IsNullOrWhiteSpace(candidate.Title) || string.Equals(candidate.Title.Trim(), id, StringComparison.Ordinal) ? string.Empty : candidate.Title.Trim(),
                    IsAxis = candidate.IsAxis,
                    State = "new",
                    Source = candidate.Source ?? string.Empty
                };
                entries.Add(entry);
                added.Add(entry);
                known.Add(Key(plane, id));
            }
            if (added.Count > 0)
                Save(entries);
            return added;
        }

        // DCS's command ids: "d<down>p<pressed>u<up>cd<device>vd<value>vp<value>vu<value>" for keys, "a<action>cd<device>"
        // for axes (pieces "nil" when not set).
        private static bool LooksLikeDcsId(string id)
        {
            string t = (id ?? string.Empty).Trim();
            if (t.Length < 3 || t.IndexOfAny(new[] { ' ', '\t', '"', '<', '>' }) >= 0)
                return false;
            return t[0] == 'd' || t[0] == 'a';
        }

        // After a DCS command list refresh: the commands the new lists have are no longer kept here; the others stay as
        // "kept" (the rebuild did not bring them, so it is not suggested again). planes: the refreshed aircraft (null =
        // all).
        public static void AfterRefresh(ICollection<string> planes)
        {
            List<Entry> entries = Load();
            if (entries.Count == 0)
                return;
            List<Entry> next = new List<Entry>();
            Dictionary<string, HashSet<string>> listIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (Entry e in entries)
            {
                Entry copy = Copy(e);
                bool refreshed = planes == null || planes.Count == 0 || planes.Contains(e.Plane, StringComparer.OrdinalIgnoreCase);
                if (refreshed)
                {
                    HashSet<string> ids;
                    if (!listIds.TryGetValue(e.Plane, out ids))
                    {
                        string list = GameDataPaths.DbFile("DCS", e.Plane + ".html");
                        ids = File.Exists(list) ? InternalDataManagement.DcsListIds(list) : new HashSet<string>(StringComparer.Ordinal);
                        listIds[e.Plane] = ids;
                    }
                    if (ids.Contains(e.Id))
                    {
                        changed = true;
                        continue;
                    }
                    if (copy.State != "kept")
                    {
                        copy.State = "kept";
                        changed = true;
                    }
                }
                next.Add(copy);
            }
            if (changed)
                Save(next);
        }

        // "Keep DB": the recorded commands stay known without asking for a rebuild again.
        public static void KeepAll()
        {
            List<Entry> entries = Load();
            if (entries.All(e => e.State == "kept"))
                return;
            Save(entries.Select(e => { Entry c = Copy(e); c.State = "kept"; return c; }).ToList());
        }

        // The commands recorded since the last refresh (a reason to rebuild the list).
        public static List<Entry> Pending()
        {
            return Load().Where(e => e.State == "new").ToList();
        }

        private static Entry Copy(Entry e)
        {
            return new Entry { Plane = e.Plane, Id = e.Id, Title = e.Title, IsAxis = e.IsAxis, State = e.State, Source = e.Source };
        }

        private static string Key(string plane, string id)
        {
            return (plane ?? string.Empty).Trim() + "|" + (id ?? string.Empty).Trim();
        }
    }
}
