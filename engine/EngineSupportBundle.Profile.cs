using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using JoyPro.History;
using JoyPro.Logging;

namespace JoyPro.Engine
{
    // The personal part of the support file (--include-profile, docs/logging.md "Support file"): the open profile and its
    // history as profile history restore reads it (<data>\ProfileHistory\<profile id>\: history.json, drafts.json,
    // blobs\<hash>.gz and blobs\<hash>.items.gz), in profile/ and profile/history/. Only the open profile.
    //
    // The profile is a .NET binary graph: the user folder or user name in a string inside it (a picture path, a DCS
    // folder) is replaced in the copy by rewriting that string record with its new length (ScrubProfile), and the copy is
    // read back with the profile's own deserializer before it goes in; a copy that does not read back is left out (and
    // summary.txt says so). A cleaned version gets the hash of its new content (blob, items and history.json follow), so
    // engine profile history restore works on the copy. The originals are only read.
    //
    // History over the cap (default 50 MB of stored copies): the newest versions up to the cap and the whole index;
    // summary.txt names the versions left out.
    //
    // engine support unpack <zip> --to <folder>: puts the profile and its history of a support file where profile history
    // restore finds them (<folder>\profile\<file>, <folder>\data\ProfileHistory\<id of that path>) for a maintainer, with
    // JOYPRO_DATA_DIR=<folder>\data.
    internal static partial class EngineSupportBundle
    {
        internal sealed class ProfileSource
        {
            public string Path;
            public string ZipName;
            public long Size;
            public ProfileHistoryLog Log;
            public bool HasHistory;
            public DateTime? OldestUtc;
            public int Versions;
            public int IncludedVersions;
            public long HistoryBytes;
            public long CapBytes;
            public List<int> LeftOut = new List<int>();
            public List<string> Hashes = new List<string>();
            public Dictionary<string, string> Remap = new Dictionary<string, string>(StringComparer.Ordinal);
            public List<int> NotCleaned = new List<int>();
            public bool ProfileLeftOut;
        }

        private static ProfileSource PlanProfile(Bundle bundle)
        {
            string path = bundle.Options.ProfilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                bundle.ProfileLine = "Profile: none open (no profile and no history in this file)";
                return null;
            }
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                bundle.ProfileLine = "Profile: " + Path.GetFileName(full) + " not found (no profile and no history in this file)";
                return null;
            }
            ProfileSource source = new ProfileSource { Path = full, ZipName = SafeZipPath(Path.GetFileName(full)), Size = new FileInfo(full).Length, CapBytes = bundle.Options.HistoryCapBytes };
            try
            {
                source.Log = ProfileHistory.Read(full);
                source.HasHistory = File.Exists(Path.Combine(source.Log.Folder, ProfileHistory.IndexFile));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                bundle.Notes.Add("profile history: not read: " + ex.Message);
            }
            if (source.HasHistory && source.Log.Versions.Count > 0)
            {
                source.Versions = source.Log.Versions.Count;
                source.OldestUtc = source.Log.Versions.Min(v => v.Utc);
                bool full2 = false;
                foreach (ProfileVersion version in source.Log.Versions.OrderByDescending(v => v.Utc).ThenByDescending(v => v.Id))
                {
                    if (source.Hashes.Contains(version.Hash))
                    {
                        source.IncludedVersions++;
                        continue;
                    }
                    string blob = Path.Combine(source.Log.Folder, "blobs", version.Hash + ".gz");
                    if (!File.Exists(blob))
                    {
                        source.LeftOut.Add(version.Id);
                        continue;
                    }
                    long size = new FileInfo(blob).Length + LengthOf(Path.Combine(source.Log.Folder, "blobs", version.Hash + ".items.gz"));
                    if (full2 || (source.Hashes.Count > 0 && source.HistoryBytes + size > source.CapBytes))
                    {
                        full2 = true;
                        source.LeftOut.Add(version.Id);
                        continue;
                    }
                    source.HistoryBytes += size;
                    source.Hashes.Add(version.Hash);
                    source.IncludedVersions++;
                }
                if (source.LeftOut.Count > 0)
                    bundle.Notes.Add("profile history: " + source.LeftOut.Count.ToString(CultureInfo.InvariantCulture) + " older version(s) left out (cap " + FormatBytes(source.CapBytes) +
                                     " of stored copies; the index lists them): " + Ranges(source.LeftOut));
            }
            bundle.ProfileLine = ProfileLine(source);
            return source;
        }

        private static string ProfileLine(ProfileSource source)
        {
            string history = !source.HasHistory
                ? "no history"
                : "history: " + source.Versions.ToString(CultureInfo.InvariantCulture) + " version(s), " + (source.IncludedVersions - CountNotCleaned(source)).ToString(CultureInfo.InvariantCulture) + " in this file" +
                  (source.LeftOut.Count > 0 ? ", " + source.LeftOut.Count.ToString(CultureInfo.InvariantCulture) + " left out by the size cap (" + Ranges(source.LeftOut) + ")" : string.Empty) +
                  (source.NotCleaned.Count > 0 ? ", " + source.NotCleaned.Count.ToString(CultureInfo.InvariantCulture) + " left out because they could not be cleaned (" + Ranges(source.NotCleaned) + ")" : string.Empty);
            return "Profile: " + source.ZipName + " (" + FormatBytes(source.Size) + (source.ProfileLeftOut ? ", left out: it could not be cleaned" : string.Empty) + "), " + history;
        }

        private static int CountNotCleaned(ProfileSource source)
        {
            return source.NotCleaned.Count;
        }

        private static void AddProfile(Bundle bundle)
        {
            ProfileSource source = bundle.Profile;
            bundle.Entries.Add(new Entry
            {
                Name = "profile/" + source.ZipName,
                Part = "profile",
                Size = source.Size,
                What = "the open profile (user folder and name replaced inside)",
                Writer = (zip, b) =>
                {
                    ScrubResult result = ScrubProfile(SafeFileIo.ReadAllBytes(source.Path));
                    if (result.Failed)
                    {
                        source.ProfileLeftOut = true;
                        b.Notes.Add("profile: left out, its copy could not be cleaned (" + result.Note + ")");
                    }
                    else
                        PutBytes(zip, b, "profile", "profile/" + source.ZipName, result.Bytes, true);
                    b.ProfileLine = ProfileLine(source);
                }
            });
            if (!source.HasHistory)
                return;
            bundle.Entries.Add(new Entry
            {
                Name = "profile/history/",
                Part = "profile",
                Size = source.HistoryBytes,
                What = "the profile's history: " + source.IncludedVersions.ToString(CultureInfo.InvariantCulture) + " of " + source.Versions.ToString(CultureInfo.InvariantCulture) + " version(s) and the index",
                Writer = WriteHistory
            });
        }

        // ---- JoyPro 1's profiles ------------------------------------------------------------------------------------
        // A tester who comes from JoyPro 1 has his old profiles in Documents\JoyPro (*.pr0file, with last.pr0file, the one
        // JoyPro 1 opened last): they go into joypro1/ (personal: only with --include-profile), cleaned like the open
        // profile, newest first up to JoyPro1CapBytes (summary.txt names the ones left out). The originals are only read.

        internal const long JoyPro1CapBytes = 50L * 1024 * 1024;

        // JOYPRO_JOYPRO1_DIR (tests), under the test sandbox <root>\Documents\JoyPro, else Documents\JoyPro.
        internal static string JoyPro1Folder()
        {
            string overridden = Environment.GetEnvironmentVariable("JOYPRO_JOYPRO1_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;
            if (TestSandbox.IsActive)
                return Path.Combine(TestSandbox.Root, "Documents", "JoyPro");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro");
        }

        private static void AddJoyPro1Profiles(Bundle bundle)
        {
            string folder = JoyPro1Folder();
            List<FileInfo> files;
            try
            {
                if (!Directory.Exists(folder))
                    return;
                files = new DirectoryInfo(folder).GetFiles("*.pr0file", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                bundle.Notes.Add("JoyPro 1 profiles: not read: " + ex.Message);
                return;
            }
            string open = bundle.Profile == null ? string.Empty : bundle.Profile.Path;
            long total = 0;
            int added = 0;
            List<string> leftOut = new List<string>();
            foreach (FileInfo file in files)
            {
                // The open profile is in profile/ already.
                if (open.Length > 0 && string.Equals(Path.GetFullPath(file.FullName), open, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (added > 0 && total + file.Length > JoyPro1CapBytes)
                {
                    leftOut.Add(file.Name);
                    continue;
                }
                total += file.Length;
                added++;
                string source = file.FullName;
                string name = "joypro1/" + SafeZipPath(file.Name);
                bundle.Entries.Add(new Entry
                {
                    Name = name,
                    Part = "joypro1",
                    Size = file.Length,
                    What = file.Name.Equals("last.pr0file", StringComparison.OrdinalIgnoreCase)
                        ? "JoyPro 1's last opened profile (user folder and name replaced inside)"
                        : "a JoyPro 1 profile (user folder and name replaced inside)",
                    Writer = (zip, b) =>
                    {
                        ScrubResult result = ScrubProfile(SafeFileIo.ReadAllBytes(source));
                        if (result.Failed)
                            b.Notes.Add("JoyPro 1 profile " + Path.GetFileName(source) + ": left out, its copy could not be cleaned (" + result.Note + ")");
                        else
                            PutBytes(zip, b, "joypro1", name, result.Bytes, true);
                    }
                });
            }
            if (leftOut.Count > 0)
                bundle.Notes.Add("JoyPro 1 profiles: " + leftOut.Count.ToString(CultureInfo.InvariantCulture) + " older one(s) left out (cap " + FormatBytes(JoyPro1CapBytes) + "): " +
                                 string.Join(", ", leftOut.Take(20)) + (leftOut.Count > 20 ? ", ..." : string.Empty));
        }

        private static void WriteHistory(ZipArchive zip, Bundle bundle)
        {
            ProfileSource source = bundle.Profile;
            string folder = source.Log.Folder;
            int done = 0;
            HashSet<string> dropped = new HashSet<string>(StringComparer.Ordinal);
            foreach (string hash in source.Hashes)
            {
                EngineProgress.Report(++done, source.Hashes.Count, "Cleaning the profile's history", hash);
                string blobPath = Path.Combine(folder, "blobs", hash + ".gz");
                byte[] blob = File.ReadAllBytes(blobPath);
                ScrubResult result;
                try
                {
                    result = ScrubProfile(Gunzip(blob));
                }
                catch (InvalidDataException ex)
                {
                    result = new ScrubResult { Failed = true, Note = "damaged copy: " + ex.Message };
                }
                if (result.Failed)
                {
                    dropped.Add(hash);
                    continue;
                }
                string newHash = hash;
                if (result.Changed)
                {
                    newHash = ProfileHistory.HashOf(result.Bytes);
                    blob = Gzip(result.Bytes);
                    source.Remap[hash] = newHash;
                }
                PutBytes(zip, bundle, "profile", "profile/history/blobs/" + newHash + ".gz", blob, false);
                string itemsPath = Path.Combine(folder, "blobs", hash + ".items.gz");
                if (File.Exists(itemsPath))
                {
                    byte[] items = File.ReadAllBytes(itemsPath);
                    try
                    {
                        string text = Encoding.UTF8.GetString(Gunzip(items));
                        string scrubbed = LogScrubber.Scrub(text);
                        if (!string.Equals(text, scrubbed, StringComparison.Ordinal))
                            items = Gzip(Encoding.UTF8.GetBytes(scrubbed));
                        PutBytes(zip, bundle, "profile", "profile/history/blobs/" + newHash + ".items.gz", items, false);
                    }
                    catch (InvalidDataException)
                    {
                        // Damaged items: "what changed" of that version is not known; the version itself still restores.
                    }
                }
            }
            if (dropped.Count > 0)
            {
                source.NotCleaned.AddRange(source.Log.Versions.Where(v => dropped.Contains(v.Hash)).Select(v => v.Id).OrderBy(i => i));
                bundle.Notes.Add("profile history: " + source.NotCleaned.Count.ToString(CultureInfo.InvariantCulture) + " version(s) left out, their copies could not be cleaned: " + Ranges(source.NotCleaned));
            }
            string index = ReadText(Path.Combine(folder, ProfileHistory.IndexFile));
            index = LogScrubber.Scrub(index);
            foreach (KeyValuePair<string, string> pair in source.Remap)
                index = index.Replace(pair.Key, pair.Value);
            PutBytes(zip, bundle, "profile", "profile/history/" + ProfileHistory.IndexFile, new UTF8Encoding(false).GetBytes(index), true);
            string drafts = Path.Combine(folder, ProfileHistory.DraftsFile);
            if (File.Exists(drafts))
                Put(zip, bundle, "profile", "profile/history/" + ProfileHistory.DraftsFile, ReadText(drafts));
            bundle.ProfileLine = ProfileLine(source);
        }

        // ---- cleaning a profile's strings -----------------------------------------------------------------------------

        internal sealed class ScrubResult
        {
            public byte[] Bytes;
            public bool Changed;
            public bool Failed;
            public string Note = string.Empty;
        }

        // Tests: the values the profile is checked for (null: the user folder, user name and PC name of this PC, as
        // LogScrubber uses them).
        internal static string[] NeedlesOverride;

        // The profile with every string record (NRBF BinaryObjectString: 0x06, object id, 7-bit length, UTF-8) that
        // LogScrubber changes rewritten; the result is read back with the profile's deserializer.
        internal static ScrubResult ScrubProfile(byte[] raw)
        {
            ScrubResult result = new ScrubResult { Bytes = raw };
            if (raw == null || raw.Length == 0 || !MayNeedScrub(raw))
                return result;
            UTF8Encoding strict = new UTF8Encoding(false, true);
            List<int[]> candidates = new List<int[]>();
            List<string> replacements = new List<string>();
            for (int i = 0; i + 6 < raw.Length; i++)
            {
                if (raw[i] != 0x06)
                    continue;
                int position = i + 5;
                int length;
                if (!ReadLength(raw, ref position, out length) || length <= 0 || position + length > raw.Length)
                    continue;
                int end = position + length;
                if (end < raw.Length && raw[end] > 22)
                    continue;
                string text;
                try
                {
                    text = strict.GetString(raw, position, length);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                string scrubbed = LogScrubber.Scrub(text);
                if (string.Equals(text, scrubbed, StringComparison.Ordinal))
                    continue;
                candidates.Add(new[] { i, i + 5, position, end, replacements.Count });
                replacements.Add(scrubbed);
            }
            if (candidates.Count == 0)
                return result;
            // The innermost record of overlapping candidates is the real one (the string's own header is next to it).
            List<int[]> accepted = new List<int[]>();
            foreach (int[] candidate in candidates.OrderByDescending(c => c[0]))
            {
                if (!accepted.Any(a => candidate[0] < a[3] && a[0] < candidate[3]))
                    accepted.Add(candidate);
            }
            using (MemoryStream output = new MemoryStream(raw.Length + 256))
            {
                int copied = 0;
                foreach (int[] edit in accepted.OrderBy(a => a[0]))
                {
                    output.Write(raw, copied, edit[1] - copied);
                    byte[] text = Encoding.UTF8.GetBytes(replacements[edit[4]]);
                    WriteLength(output, text.Length);
                    output.Write(text, 0, text.Length);
                    copied = edit[3];
                }
                output.Write(raw, copied, raw.Length - copied);
                result.Bytes = output.ToArray();
            }
            try
            {
                using (MemoryStream check = new MemoryStream(result.Bytes))
                {
                    BinaryFormatter formatter = new BinaryFormatter { Binder = new LegacyJoyProSerializationBinder() };
                    if (!(formatter.Deserialize(check) is Pr0file))
                        throw new InvalidDataException("not a profile");
                }
            }
            catch (Exception ex)
            {
                JoyProLog.Warn("Support", "A profile copy for the support file did not read back after cleaning (" + accepted.Count.ToString(CultureInfo.InvariantCulture) + " string(s) changed)", ex);
                return new ScrubResult { Bytes = raw, Failed = true, Note = ex.GetType().Name };
            }
            result.Changed = true;
            result.Note = accepted.Count.ToString(CultureInfo.InvariantCulture) + " string(s) cleaned";
            return result;
        }

        // Quick check (ASCII case-insensitive bytes): does the profile hold the user folder, user name, PC name or a Steam id?
        private static bool MayNeedScrub(byte[] raw)
        {
            List<string> needles = NeedlesOverride != null ? NeedlesOverride.ToList() : new List<string>();
            if (NeedlesOverride == null)
            {
                string profile = (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty).TrimEnd('\\', '/');
                if (profile.Length >= 4)
                {
                    needles.Add(profile);
                    needles.Add(profile.Replace('\\', '/'));
                }
                if ((Environment.UserName ?? string.Empty).Trim().Length >= 2)
                    needles.Add(Environment.UserName.Trim());
                if ((Environment.MachineName ?? string.Empty).Trim().Length >= 3)
                    needles.Add(Environment.MachineName.Trim());
            }
            needles.Add("7656119");
            byte[] lower = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                lower[i] = raw[i] >= (byte)'A' && raw[i] <= (byte)'Z' ? (byte)(raw[i] + 32) : raw[i];
            foreach (string needle in needles.Where(n => !string.IsNullOrEmpty(n)))
            {
                byte[] pattern = Encoding.UTF8.GetBytes(needle.ToLowerInvariant());
                if (IndexOf(lower, pattern) >= 0)
                    return true;
            }
            return false;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length)
                return -1;
            byte first = needle[0];
            int last = haystack.Length - needle.Length;
            for (int i = Array.IndexOf(haystack, first, 0); i >= 0 && i <= last; i = Array.IndexOf(haystack, first, i + 1))
            {
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }

        private static bool ReadLength(byte[] raw, ref int position, out int length)
        {
            length = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (position >= raw.Length)
                    return false;
                byte b = raw[position++];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return length >= 0;
            }
            return false;
        }

        private static void WriteLength(Stream output, int length)
        {
            uint value = (uint)length;
            while (value >= 0x80)
            {
                output.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            output.WriteByte((byte)value);
        }

        private static byte[] Gunzip(byte[] content)
        {
            using (MemoryStream input = new MemoryStream(content))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] Gzip(byte[] content)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                    gzip.Write(content, 0, content.Length);
                return output.ToArray();
            }
        }

        private static long LengthOf(string path)
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        // "1-20, 25, 31-33".
        internal static string Ranges(IEnumerable<int> ids)
        {
            List<int> sorted = ids.Distinct().OrderBy(i => i).ToList();
            List<string> parts = new List<string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                int start = sorted[i];
                while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
                    i++;
                parts.Add(start == sorted[i] ? "#" + start.ToString(CultureInfo.InvariantCulture) : "#" + start.ToString(CultureInfo.InvariantCulture) + "-#" + sorted[i].ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(", ", parts);
        }

        // ---- engine support unpack ----------------------------------------------------------------------------------

        private static int RunUnpack(List<string> rest, bool textOutput, out string payload)
        {
            string to = PopValue(rest, "--to");
            string zipPath = rest.Count == 1 ? rest[0] : null;
            if (string.IsNullOrWhiteSpace(zipPath) || string.IsNullOrWhiteSpace(to))
            {
                payload = "Use: engine support unpack <support zip> --to <folder>.";
                return 1;
            }
            zipPath = Path.GetFullPath(zipPath);
            if (!File.Exists(zipPath))
            {
                payload = "Support file not found: " + zipPath;
                return 1;
            }
            string dest = Path.GetFullPath(to);
            string support = Path.Combine(dest, "support");
            string profile = null;
            string historyFolder = null;
            int historyFiles = 0;
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry profileEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("profile/", StringComparison.Ordinal) && e.FullName.IndexOf('/', "profile/".Length) < 0 &&
                                                                                e.FullName.EndsWith(".pr0file", StringComparison.OrdinalIgnoreCase));
                if (profileEntry != null)
                {
                    profile = Path.Combine(dest, "profile", profileEntry.Name);
                    historyFolder = Path.Combine(dest, "data", ProfileHistory.FolderName, ProfileHistory.IdFor(profile));
                }
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        continue;
                    Extract(entry, Path.Combine(support, entry.FullName.Replace('/', '\\')), support);
                    if (entry == profileEntry)
                        Extract(entry, profile, dest);
                    else if (historyFolder != null && entry.FullName.StartsWith("profile/history/", StringComparison.Ordinal))
                    {
                        Extract(entry, Path.Combine(historyFolder, entry.FullName.Substring("profile/history/".Length).Replace('/', '\\')), historyFolder);
                        historyFiles++;
                    }
                }
            }
            string data = Path.Combine(dest, "data");
            // The index names the profile where it was on the tester's PC: point it at the copy.
            string index = historyFolder == null ? null : Path.Combine(historyFolder, ProfileHistory.IndexFile);
            if (index != null && File.Exists(index))
            {
                string json = File.ReadAllText(index, Encoding.UTF8);
                json = System.Text.RegularExpressions.Regex.Replace(json, "\"profilePath\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"",
                    m => "\"profilePath\": \"" + profile.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                File.WriteAllText(index, json, new UTF8Encoding(false));
            }
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Support = support, Profile = profile ?? string.Empty, DataFolder = data, HistoryFolder = historyFolder ?? string.Empty, HistoryFiles = historyFiles });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Unpacked: " + support);
            if (profile == null)
                sb.AppendLine("This support file holds no profile.");
            else
            {
                sb.AppendLine("Profile: " + profile);
                sb.AppendLine("History: " + (historyFiles == 0 ? "none" : historyFolder + " (" + historyFiles.ToString(CultureInfo.InvariantCulture) + " files)"));
                sb.AppendLine("Next (an isolated data folder, nothing of this PC's JoyPro is touched):");
                sb.AppendLine("  $env:JOYPRO_DATA_DIR = '" + data + "'");
                sb.AppendLine("  engine --profile '" + profile + "' profile history list --text");
                sb.AppendLine("  engine --profile '" + profile + "' profile history restore <id> --text");
            }
            payload = sb.ToString();
            return 0;
        }

        private static void Extract(ZipArchiveEntry entry, string target, string root)
        {
            string full = Path.GetFullPath(target);
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A zip entry points outside the folder: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (Stream input = entry.Open())
            using (FileStream output = new FileStream(full, FileMode.Create, FileAccess.Write))
                input.CopyTo(output);
        }
    }
}
