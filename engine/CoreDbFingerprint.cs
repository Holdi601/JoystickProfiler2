using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JoyPro.Engine;

namespace JoyPro
{
    internal static class CoreDbFingerprint
    {
        internal sealed class FingerprintResult
        {
            public string Fingerprint { get; set; }
            public int FileCount { get; set; }
            public long TotalBytes { get; set; }
            public List<string> IncludedRoots { get; set; }
            public List<string> Warnings { get; set; }

            public FingerprintResult()
            {
                Fingerprint = string.Empty;
                FileCount = 0;
                TotalBytes = 0;
                IncludedRoots = new List<string>();
                Warnings = new List<string>();
            }
        }

        public static FingerprintResult Compute(string programRoot)
        {
            FingerprintResult result = new FingerprintResult();
            string root = NormalizeRoot(programRoot);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                result.Warnings.Add("Program root missing: " + (programRoot ?? string.Empty));
                result.Fingerprint = "sha256:" + new string('0', 64);
                return result;
            }

            // The data JoyPro reads: DB, CleanProfile and KeyboardCleanProfile of the program folder, each game's folder
            // taken from where GameDataPaths reads it (the game-data folder first). Paths are hashed relative to their
            // root, so the same lists give the same fingerprint wherever they are.
            string userRoot = GameDataPaths.UserRoot();
            List<KeyValuePair<string, string>> allFiles = new List<KeyValuePair<string, string>>();
            foreach (string top in new[] { "DB", "CleanProfile", "KeyboardCleanProfile" })
            {
                string programTop = Path.Combine(root, top);
                string userTop = Path.Combine(userRoot, top);
                if (!Directory.Exists(programTop) && !Directory.Exists(userTop))
                {
                    result.Warnings.Add("Fingerprint source folder missing: " + programTop);
                    continue;
                }

                List<string> folders = new List<string>();
                if (Directory.Exists(programTop))
                {
                    result.IncludedRoots.Add(programTop);
                    foreach (string file in Directory.GetFiles(programTop))
                        allFiles.Add(new KeyValuePair<string, string>(root, file));
                    folders.AddRange(Directory.GetDirectories(programTop).Select(Path.GetFileName));
                }
                if (Directory.Exists(userTop))
                {
                    result.IncludedRoots.Add(userTop);
                    folders.AddRange(Directory.GetDirectories(userTop).Select(Path.GetFileName));
                }

                foreach (string name in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string folder;
                    if (top == "DB")
                        folder = GameDataPaths.DbFolder(name);
                    else
                        folder = GameDataPaths.CleanProfileFolder(name, top == "KeyboardCleanProfile");
                    if (!Directory.Exists(folder))
                        continue;
                    string baseRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\')));
                    foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        // source-version.txt only stamps which game build a list belongs to; the update check writes
                        // it (also as a first baseline) without changing a list. Counting it made the next save of a
                        // profile run the full load correction and add every same-id aircraft to every relation.
                        if (string.Equals(Path.GetFileName(file), GameDataPaths.StampFile, StringComparison.OrdinalIgnoreCase))
                            continue;
                        allFiles.Add(new KeyValuePair<string, string>(baseRoot, file));
                    }
                }
            }

            List<KeyValuePair<string, string>> ordered = allFiles
                .Select(f => new KeyValuePair<string, string>(MakeRelativePath(f.Key, f.Value).Replace('\\', '/').ToLowerInvariant(), f.Value))
                .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.FileCount = ordered.Count;
            // Each file's content hash, read only for files new or changed since the last fingerprint (FileHashes:
            // size and time stamp); the files to read are hashed several at once. The fingerprint is the same as from
            // reading every file (27 MB, about a second, on every profile load and save with load corrections).
            byte[][] hashes = new byte[ordered.Count][];
            long[] lengths = new long[ordered.Count];
            string[] errors = new string[ordered.Count];
            FileHashes.Begin();
            System.Threading.Tasks.Parallel.For(0, ordered.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount)) }, i =>
            {
                try
                {
                    hashes[i] = FileHashes.Get(ordered[i].Value, out lengths[i]);
                }
                catch (Exception ex)
                {
                    hashes[i] = new byte[0];
                    lengths[i] = 0L;
                    errors[i] = "Could not hash file '" + ordered[i].Value + "': " + ex.Message;
                }
            });
            FileHashes.End();
            using (SHA256 aggregate = SHA256.Create())
            {
                AppendUtf8(aggregate, "joypro-db-fingerprint-v1");
                for (int i = 0; i < ordered.Count; i++)
                {
                    string relativePath = ordered[i].Key;
                    AppendUtf8(aggregate, relativePath);
                    if (errors[i] != null)
                        result.Warnings.Add(errors[i]);
                    long fileLength = lengths[i];
                    result.TotalBytes += Math.Max(0L, fileLength);
                    AppendInt64(aggregate, fileLength);
                    AppendBytes(aggregate, hashes[i]);
                }

                aggregate.TransformFinalBlock(new byte[0], 0, 0);
                result.Fingerprint = "sha256:" + ToHex(aggregate.Hash);
            }

            return result;
        }

        // SHA-256 of a file's content, kept per path with the file's size and time stamp in memory and in
        // <data>\Cache\db-file-hashes.tsv (JOYPRO_FUNCTION_MAP_CACHE=off: memory only).
        internal static class FileHashes
        {
            private sealed class Entry
            {
                public long Length;
                public long Ticks;
                public byte[] Hash;
            }

            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> Known =
                new System.Collections.Concurrent.ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            private static readonly object Gate = new object();
            private static string loadedFrom;
            private static int changed;

            private static string CacheFile()
            {
                return Path.Combine(GameInstalls.DataRoot(), "Cache", "db-file-hashes.tsv");
            }

            public static void Begin()
            {
                lock (Gate)
                {
                    string file = CacheFile();
                    if (string.Equals(loadedFrom, file, StringComparison.OrdinalIgnoreCase))
                        return;
                    loadedFrom = file;
                    Known.Clear();
                    changed = 0;
                    if (EngineFunctionMapCache.Disabled || !File.Exists(file))
                        return;
                    try
                    {
                        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            string[] parts = line.Split('\t');
                            long length, ticks;
                            if (parts.Length != 4 || !long.TryParse(parts[1], out length) || !long.TryParse(parts[2], out ticks) || parts[3].Length != 64)
                                continue;
                            Known[parts[0]] = new Entry { Length = length, Ticks = ticks, Hash = FromHex(parts[3]) };
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                    {
                        Known.Clear();
                    }
                }
            }

            public static byte[] Get(string path, out long length)
            {
                FileInfo info = new FileInfo(path);
                long ticks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
                Entry known;
                if (Known.TryGetValue(path, out known) && known.Length == length && known.Ticks == ticks)
                    return known.Hash;
                byte[] hash;
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 contentHash = SHA256.Create())
                {
                    length = stream.Length;
                    hash = contentHash.ComputeHash(stream);
                }
                // Kept only when the file did not change while it was read.
                FileInfo after = new FileInfo(path);
                if (after.Length == length && after.LastWriteTimeUtc.Ticks == ticks)
                {
                    Known[path] = new Entry { Length = length, Ticks = ticks, Hash = hash };
                    System.Threading.Interlocked.Increment(ref changed);
                }
                return hash;
            }

            public static void End()
            {
                lock (Gate)
                {
                    if (changed == 0 || EngineFunctionMapCache.Disabled)
                        return;
                    changed = 0;
                    try
                    {
                        string file = CacheFile();
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        StringBuilder sb = new StringBuilder();
                        foreach (KeyValuePair<string, Entry> pair in Known.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!File.Exists(pair.Key))
                                continue;
                            sb.Append(pair.Key).Append('\t').Append(pair.Value.Length).Append('\t').Append(pair.Value.Ticks).Append('\t').Append(ToHex(pair.Value.Hash).ToLowerInvariant()).Append('\n');
                        }
                        string temp = file + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                        if (File.Exists(file))
                            File.Replace(temp, file, null);
                        else
                            File.Move(temp, file);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Next time the files are read again.
                    }
                }
            }

            private static byte[] FromHex(string hex)
            {
                byte[] bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return bytes;
            }
        }

        private static string NormalizeRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return string.Empty;
            try
            {
                return Path.GetFullPath(rootPath.Trim());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string MakeRelativePath(string rootPath, string fullPath)
        {
            Uri rootUri = new Uri(AppendDirectorySeparator(rootPath));
            Uri fileUri = new Uri(Path.GetFullPath(fullPath));
            string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString());
            return relative.Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path ?? string.Empty;
            char separator = Path.DirectorySeparatorChar;
            if (path[path.Length - 1] == separator || path[path.Length - 1] == Path.AltDirectorySeparatorChar)
                return path;
            return path + separator;
        }

        private static void AppendUtf8(HashAlgorithm hash, string value)
        {
            string text = value ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            AppendInt32(hash, bytes.Length);
            AppendBytes(hash, bytes);
        }

        private static void AppendInt32(HashAlgorithm hash, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            AppendBytes(hash, bytes);
        }

        private static void AppendInt64(HashAlgorithm hash, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            AppendBytes(hash, bytes);
        }

        private static void AppendBytes(HashAlgorithm hash, byte[] bytes)
        {
            byte[] payload = bytes ?? new byte[0];
            if (payload.Length == 0)
                return;
            hash.TransformBlock(payload, 0, payload.Length, null, 0);
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return new string('0', 64);

            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
