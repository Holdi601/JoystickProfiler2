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
            using (SHA256 aggregate = SHA256.Create())
            {
                AppendUtf8(aggregate, "joypro-db-fingerprint-v1");
                for (int i = 0; i < ordered.Count; i++)
                {
                    string filePath = ordered[i].Value;
                    string relativePath = ordered[i].Key;
                    AppendUtf8(aggregate, relativePath);

                    byte[] fileHash = new byte[0];
                    long fileLength = 0L;
                    try
                    {
                        using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (SHA256 contentHash = SHA256.Create())
                        {
                            fileLength = stream.Length;
                            fileHash = contentHash.ComputeHash(stream);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add("Could not hash file '" + filePath + "': " + ex.Message);
                    }

                    result.TotalBytes += Math.Max(0L, fileLength);
                    AppendInt64(aggregate, fileLength);
                    AppendBytes(aggregate, fileHash);
                }

                aggregate.TransformFinalBlock(new byte[0], 0, 0);
                result.Fingerprint = "sha256:" + ToHex(aggregate.Hash);
            }

            return result;
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
