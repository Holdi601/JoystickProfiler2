using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JoyPro.Engine
{
    // The text files of one engine run, read once (docs/testing.md "Speed"). A DCS export reads the Saved Games input
    // files, writes some of them and then reads the whole folder again to record JoyPro's state (EngineSync); the
    // import reads every .jp backup's .diff.lua twice and the same clean profile for every device. Here a file is read
    // once, many at once (Prefetch), and a file the run wrote is taken from what it wrote. Every hit is checked against
    // the file's size and time stamp, so a file changed meanwhile by anyone else is read again.
    internal static class RunFileCache
    {
        private sealed class Entry
        {
            public long Length;
            public DateTime WriteUtc;
            public string Text;
        }

        private static readonly ConcurrentDictionary<string, Entry> Texts = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string[]> Listings = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // A new run (Program.Run) starts with nothing cached.
        public static void Clear()
        {
            Texts.Clear();
            Listings.Clear();
        }

        private static string Key(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path ?? string.Empty;
            }
        }

        // File.ReadAllText(path) (UTF-8 unless the file has another byte order mark), cached.
        public static string ReadAllText(string path)
        {
            string key = Key(path);
            FileInfo info = new FileInfo(key);
            Entry entry;
            if (Texts.TryGetValue(key, out entry) && info.Exists && entry.Length == info.Length && entry.WriteUtc == info.LastWriteTimeUtc)
                return entry.Text;
            return Load(key);
        }

        private static string Load(string key)
        {
            // Stamp before the read: a write during the read makes the next stamp check fail, never a stale hit.
            FileInfo before = new FileInfo(key);
            DateTime writeUtc = before.LastWriteTimeUtc;
            long length = before.Exists ? before.Length : -1;
            string text = File.ReadAllText(key);
            Texts[key] = new Entry { Length = length, WriteUtc = writeUtc, Text = text };
            return text;
        }

        // File.WriteAllText(path, text, UTF-8 without BOM), remembered as what a later read of it returns.
        public static void WriteAllText(string path, string text)
        {
            File.WriteAllText(path, text, Utf8NoBom);
            Wrote(path, text);
        }

        // A file the run wrote as UTF-8 without byte order mark: a read in this run gets the text back as File.ReadAllText
        // would decode it (characters UTF-8 cannot hold come back as U+FFFD, as from the disk).
        public static void Wrote(string path, string text)
        {
            string key = Key(path);
            try
            {
                FileInfo info = new FileInfo(key);
                if (!info.Exists)
                {
                    Texts.TryRemove(key, out _);
                    return;
                }
                string decoded = text ?? string.Empty;
                byte[] bytes = Utf8NoBom.GetBytes(decoded);
                if (bytes.Length != info.Length || StartsWithByteOrderMark(bytes))
                {
                    Texts.TryRemove(key, out _);
                    return;
                }
                decoded = Utf8NoBom.GetString(bytes);
                Texts[key] = new Entry { Length = info.Length, WriteUtc = info.LastWriteTimeUtc, Text = decoded };
            }
            catch (Exception)
            {
                Texts.TryRemove(key, out _);
            }
        }

        private static bool StartsWithByteOrderMark(byte[] bytes)
        {
            // File.ReadAllText would take a leading BOM-looking sequence as the encoding: read such a file from disk.
            return (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ||
                   (bytes.Length >= 2 && ((bytes[0] == 0xFE && bytes[1] == 0xFF) || (bytes[0] == 0xFF && bytes[1] == 0xFE)));
        }

        // A file the run deleted or moved away.
        public static void Forget(string path)
        {
            Texts.TryRemove(Key(path), out _);
        }

        // Reads the files not cached yet, several at once (opening a file is the slow part on Windows, virus scanners
        // look at every one). Unreadable files are left for the caller's own read and its error message.
        public static void Prefetch(IEnumerable<string> paths)
        {
            List<string> missing = (paths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(k => !Texts.ContainsKey(k)).ToList();
            if (missing.Count == 0)
                return;
            int workers = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = workers }, key =>
            {
                try
                {
                    if (File.Exists(key))
                        Load(key);
                }
                catch (Exception)
                {
                }
            });
        }

        // Directory.GetFiles(folder, pattern) (top folder only), once per run and folder; for folders the run does not
        // write (the clean profiles of the DCS lists).
        public static string[] ListFiles(string folder, string pattern)
        {
            string key = Key(folder) + "|" + pattern;
            return Listings.GetOrAdd(key, _ => Directory.GetFiles(folder, pattern));
        }
    }
}
