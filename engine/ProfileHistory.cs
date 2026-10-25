#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using JoyPro.DeviceReference;

namespace JoyPro.History
{
    // Profile history (docs/profile-history.md): every save of a profile becomes a version in
    // <data>\ProfileHistory\<profile id>\ (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro, like the engine's other data).
    //
    //   history.json        the versions (id, time, kind, label, summary, content hash, kept, parent), the undo cursor,
    //                       the redo stack and the time of the last clean-up
    //   blobs\<hash>.gz     one gzip-compressed copy per distinct profile content (versions with the same content share it:
    //                       an undo, a redo or a restore costs no space)
    //   blobs\<hash>.items.gz  the profile's items (relations, binds, modifiers, settings sections) as short lines with
    //                       hashes, written by the engine; "what changed" between two versions comes from them
    //   drafts.json         what the GUI holds that is not in the profile yet (modifiers without a bind)
    //
    // Undo / redo walk the versions like an editor's undo: a change's parent is the version it was made on; an undo
    // writes the parent's content back and records an "undo" version pointing at it (so an undo is itself in the
    // history), the undone version goes on the redo stack; any new change clears the redo stack. A restore of an older
    // version is a change of its own: undo after a restore returns to the state before the restore.
    //
    // Retention (Prune): every version of the last 24 hours, then the newest per hour for 7 days, per day for 60 days and
    // per week after that; then the oldest versions go until the profile's history is under 200 MB, and the oldest of all
    // profiles until all histories are under 1 GB. Kept versions ("Keep"), the newest one and the undo cursor are never
    // removed.
    //
    // Shared by the engine (.NET Framework, which records versions when it saves a profile) and the GUI (.NET 9, linked
    // file: history panel, undo / redo, restore, clean-up). Plain C# 7.3. Several processes may use one history at the same
    // time (the GUI, its engine processes, the tray host): every change holds the folder's .lock file.
    public sealed class ProfileHistoryPolicy
    {
        public double KeepEverythingHours = 24;
        public int HourlyDays = 7;
        public int DailyDays = 60;
        public long ProfileCapBytes = 200L * 1024 * 1024;
        public long GlobalCapBytes = 1024L * 1024 * 1024;
        // Saves within this many seconds of the previous one become one version (one click that runs several commands).
        public double CoalesceSeconds = 2;
        // A save cleans up the profile's history when the last clean-up is this old.
        public double AutoPruneHours = 6;

        public static ProfileHistoryPolicy Default
        {
            get { return new ProfileHistoryPolicy(); }
        }
    }

    public static class ProfileVersionKind
    {
        public const string Change = "change";
        public const string Open = "open";
        public const string External = "external";
        public const string Checkpoint = "checkpoint";
        public const string Restore = "restore";
        public const string Undo = "undo";
        public const string Redo = "redo";
    }

    public sealed class ProfileVersion
    {
        public int Id;
        public DateTime Utc;
        public string Kind = ProfileVersionKind.Change;
        public string Label = string.Empty;
        public string Summary = string.Empty;
        public string Hash = string.Empty;
        public long Size;
        public bool Keep;
        // The version this one was made on (0: none known).
        public int Parent;
        // Undo / redo / restore: the version whose content was written back.
        public int From;

        public string Title
        {
            get
            {
                string label = (Label ?? string.Empty).Trim();
                if (label.Length > 0)
                    return label;
                switch (Kind)
                {
                    case ProfileVersionKind.Open: return "First version";
                    case ProfileVersionKind.External: return "Changed outside JoyPro";
                    case ProfileVersionKind.Checkpoint: return "Checkpoint";
                    case ProfileVersionKind.Restore: return "Restored an older version";
                    case ProfileVersionKind.Undo: return "Undo";
                    case ProfileVersionKind.Redo: return "Redo";
                    default: return "Change";
                }
            }
        }

        // The label plus what changed, for tooltips and the CLI.
        public string Description
        {
            get
            {
                string summary = (Summary ?? string.Empty).Trim();
                return summary.Length == 0 || string.Equals(summary, Title, StringComparison.Ordinal) ? Title : Title + ": " + summary;
            }
        }
    }

    public sealed class ProfileHistoryRecordOptions
    {
        public string Kind = ProfileVersionKind.Change;
        public string Label = string.Empty;
        // A fixed summary; null: what changed against the version it was made on (from the items).
        public string Summary;
        // The profile's items (relations, binds, ...); asked for only when this content is new to the history.
        public Func<IList<string>> Items;
        // The content of the profile file before this save (a moved profile's history is found by it).
        public string PreviousHash;
        public int From;
        public bool Coalesce = true;
    }

    public sealed class ProfileHistoryLog
    {
        public string Folder = string.Empty;
        public string ProfilePath = string.Empty;
        public List<ProfileVersion> Versions = new List<ProfileVersion>();
        public int Cursor;
        public List<int> Redo = new List<int>();
        public DateTime LastPruneUtc = DateTime.MinValue;
        public int NextId = 1;

        public ProfileVersion Find(int id)
        {
            if (id <= 0)
                return null;
            for (int i = Versions.Count - 1; i >= 0; i--)
            {
                if (Versions[i].Id == id)
                    return Versions[i];
            }
            return null;
        }

        public ProfileVersion Latest
        {
            get { return Versions.Count == 0 ? null : Versions[Versions.Count - 1]; }
        }

        // The version the profile is at (the newest when the cursor is unknown).
        public ProfileVersion CursorVersion
        {
            get { return Find(Cursor) ?? Latest; }
        }

        // An undo or redo version stands for the version it wrote back.
        public ProfileVersion Logical(ProfileVersion version)
        {
            ProfileVersion current = version;
            for (int guard = 0; current != null && guard < 10000; guard++)
            {
                if ((current.Kind != ProfileVersionKind.Undo && current.Kind != ProfileVersionKind.Redo) || current.From <= 0)
                    return current;
                ProfileVersion next = Find(current.From);
                if (next == null)
                    return current;
                current = next;
            }
            return current;
        }

        // What an undo goes back to (null: nothing).
        public ProfileVersion UndoTarget
        {
            get
            {
                ProfileVersion logical = Logical(CursorVersion);
                return logical == null ? null : Find(logical.Parent);
            }
        }

        // What an undo takes back (the change made last).
        public ProfileVersion UndoSubject
        {
            get { return UndoTarget == null ? null : Logical(CursorVersion); }
        }

        public ProfileVersion RedoTarget
        {
            get
            {
                for (int i = Redo.Count - 1; i >= 0; i--)
                {
                    ProfileVersion version = Find(Redo[i]);
                    if (version != null)
                        return version;
                }
                return null;
            }
        }

        public bool CanUndo
        {
            get { return UndoTarget != null; }
        }

        public bool CanRedo
        {
            get { return RedoTarget != null; }
        }
    }

    public sealed class ProfileHistoryUsage
    {
        public int Profiles;
        public int Versions;
        public int Kept;
        public long Bytes;
    }

    public sealed class ProfileHistoryPruneResult
    {
        public List<ProfileVersion> Removed = new List<ProfileVersion>();
        public long BytesBefore;
        public long BytesAfter;
        public int VersionsLeft;
        public bool DryRun;
    }

    public sealed class ProfileHistoryWriteResult
    {
        public bool Changed;
        public string Message = string.Empty;
        public ProfileVersion Version;
        // The version whose content is now in the profile file.
        public ProfileVersion Target;
    }

    public static class ProfileHistory
    {
        public const string FolderName = "ProfileHistory";
        public const string IndexFile = "history.json";
        public const string DraftsFile = "drafts.json";
        private const string BlobFolder = "blobs";
        private const string LockFile = ".lock";
        private const string ItemsHeader = "#joypro-profile-items 1";

        // Tests: the clock (simulated timestamps for the retention) and the root folder.
        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;
        public static string RootOverride;

        // JOYPRO_PROFILE_HISTORY=off switches recording off (nothing else reads or writes then).
        public static bool Enabled
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("JOYPRO_PROFILE_HISTORY");
                return !string.Equals((value ?? string.Empty).Trim(), "off", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Root()
        {
            if (!string.IsNullOrWhiteSpace(RootOverride))
                return RootOverride;
            string data = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            if (string.IsNullOrWhiteSpace(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            return Path.Combine(data, FolderName);
        }

        // "<file name>-<10 hex of the full path>": readable in Explorer, unique per profile file.
        public static string IdFor(string profilePath)
        {
            string full = NormalizePath(profilePath);
            string name = Path.GetFileNameWithoutExtension(full) ?? "profile";
            StringBuilder clean = new StringBuilder();
            foreach (char c in name)
            {
                // Short paths: the engine (.NET Framework) stops at 260 characters.
                if (clean.Length >= 32)
                    break;
                clean.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            if (clean.Length == 0)
                clean.Append("profile");
            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                return clean + "-" + ToHex(digest).Substring(0, 10);
            }
        }

        public static string FolderFor(string profilePath)
        {
            return Path.Combine(Root(), IdFor(profilePath));
        }

        public static string HashOf(byte[] content)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(content ?? new byte[0])).Substring(0, 32);
        }

        // ---- reading ----------------------------------------------------------------------------------------------

        // The profile's history as it is now (empty when there is none). No lock: the index is replaced atomically.
        public static ProfileHistoryLog Read(string profilePath)
        {
            string folder = FolderFor(profilePath);
            ProfileHistoryLog log = LoadLog(folder);
            if (string.IsNullOrWhiteSpace(log.ProfilePath))
                log.ProfilePath = NormalizePath(profilePath);
            return log;
        }

        public static byte[] ReadContent(string profilePath, int versionId)
        {
            ProfileHistoryLog log = Read(profilePath);
            ProfileVersion version = log.Find(versionId);
            if (version == null)
                throw new ArgumentException("version " + versionId.ToString(CultureInfo.InvariantCulture) + " is not in the history.");
            return ReadBlob(log.Folder, version.Hash);
        }

        // The items of a version (null: not known, e.g. a version changed outside JoyPro).
        public static IList<string> ReadItems(string profilePath, int versionId)
        {
            ProfileHistoryLog log = Read(profilePath);
            ProfileVersion version = log.Find(versionId);
            return version == null ? null : ReadItemsOf(log.Folder, version.Hash);
        }

        // What changed from one version to another, one line per change (null: one of them has no items).
        public static List<string> Diff(string profilePath, int fromId, int toId)
        {
            ProfileHistoryLog log = Read(profilePath);
            ProfileVersion from = log.Find(fromId);
            ProfileVersion to = log.Find(toId);
            if (from == null || to == null)
                return null;
            if (from.Hash == to.Hash)
                return new List<string>();
            IList<string> before = ReadItemsOf(log.Folder, from.Hash);
            IList<string> after = ReadItemsOf(log.Folder, to.Hash);
            if (before == null || after == null)
                return null;
            return DescribeChanges(before, after);
        }

        public static ProfileHistoryUsage Usage(string profilePath)
        {
            ProfileHistoryLog log = Read(profilePath);
            return new ProfileHistoryUsage
            {
                Profiles = log.Versions.Count > 0 ? 1 : 0,
                Versions = log.Versions.Count,
                Kept = log.Versions.Count(v => v.Keep),
                Bytes = FolderBytes(log.Folder)
            };
        }

        public static ProfileHistoryUsage UsageAll()
        {
            ProfileHistoryUsage usage = new ProfileHistoryUsage();
            foreach (string folder in HistoryFolders())
            {
                ProfileHistoryLog log = LoadLog(folder);
                usage.Profiles++;
                usage.Versions += log.Versions.Count;
                usage.Kept += log.Versions.Count(v => v.Keep);
                usage.Bytes += FolderBytes(folder);
            }
            return usage;
        }

        // ---- recording --------------------------------------------------------------------------------------------

        // Before several Record calls of one save (the file before it and the save itself): their content and item
        // files are written side by side (each ~0.1-0.2 s for a 4 MB profile), the records then find them. The files are
        // named by the content's hash, so writing them early changes nothing else; a history folder of a moved profile
        // is adopted first, as Record does.
        public static void PrepareVersions(string profilePath, string previousHash, params KeyValuePair<byte[], Func<IList<string>>>[] versions)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(profilePath) || versions == null || versions.Length == 0)
                return;
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
                TryAdopt(profilePath, folder, previousHash);
            System.Threading.Tasks.Parallel.ForEach(versions.Where(v => v.Key != null), version =>
            {
                try
                {
                    string hash = HashOf(version.Key);
                    EnsureBlob(folder, hash, version.Key);
                    EnsureItems(folder, hash, version.Value);
                }
                catch (Exception)
                {
                    // Record writes what is missing.
                }
            });
        }

        // Several versions of one save in one write (the file before it, then the save): one lock, one index write, and
        // the automatic clean-up only after all of them (before, it ran after the first one and deleted the second one's
        // file that PrepareVersions had just written). Returns the version the profile is at afterwards.
        public static ProfileVersion RecordVersions(string profilePath, params KeyValuePair<byte[], ProfileHistoryRecordOptions>[] versions)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(profilePath) || versions == null || versions.Length == 0)
                return null;
            ProfileHistoryPolicy policy = ProfileHistoryPolicy.Default;
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
                TryAdopt(profilePath, folder, (versions[0].Value ?? new ProfileHistoryRecordOptions()).PreviousHash);
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                log.ProfilePath = NormalizePath(profilePath);
                ProfileVersion version = null;
                foreach (KeyValuePair<byte[], ProfileHistoryRecordOptions> next in versions)
                {
                    if (next.Key != null)
                        version = RecordLocked(log, next.Key, next.Value ?? new ProfileHistoryRecordOptions(), policy);
                }
                if ((UtcNow() - log.LastPruneUtc).TotalHours >= policy.AutoPruneHours)
                    PruneLocked(log, policy, false);
                SaveLog(log);
                return version;
            }
        }

        // A save of the profile: a new version unless the content is the one the profile is at already. Returns the
        // version the profile is at afterwards (null when the history is off).
        public static ProfileVersion Record(string profilePath, byte[] content, ProfileHistoryRecordOptions options, ProfileHistoryPolicy policy = null)
        {
            if (!Enabled || content == null || string.IsNullOrWhiteSpace(profilePath))
                return null;
            options = options ?? new ProfileHistoryRecordOptions();
            policy = policy ?? ProfileHistoryPolicy.Default;
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
                TryAdopt(profilePath, folder, options.PreviousHash);
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                log.ProfilePath = NormalizePath(profilePath);
                ProfileVersion version = RecordLocked(log, content, options, policy);
                if ((UtcNow() - log.LastPruneUtc).TotalHours >= policy.AutoPruneHours)
                    PruneLocked(log, policy, false);
                SaveLog(log);
                return version;
            }
        }

        // A named version of the profile file as it is now ("Before export to DCS"); never merged into a change.
        public static ProfileVersion Checkpoint(string profilePath, string label, Func<IList<string>> items = null)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
                return null;
            byte[] content = File.ReadAllBytes(profilePath);
            return Record(profilePath, content, new ProfileHistoryRecordOptions
            {
                Kind = ProfileVersionKind.Checkpoint,
                Label = label ?? string.Empty,
                Items = items,
                PreviousHash = HashOf(content),
                Coalesce = false
            });
        }

        // The file on disk is not the version the history is at (changed by another program, an older JoyPro or a
        // copy): it becomes a version first, so an undo or restore never loses it.
        public static ProfileVersion RecordIfChangedOutside(string profilePath, Func<IList<string>> items = null)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
                return null;
            string folder = FolderFor(profilePath);
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                log.ProfilePath = NormalizePath(profilePath);
                ProfileVersion version = RecordOutsideChangeLocked(log, profilePath, items);
                SaveLog(log);
                return version;
            }
        }

        // ---- undo / redo / restore --------------------------------------------------------------------------------

        public static ProfileHistoryWriteResult Undo(string profilePath)
        {
            return WriteBack(profilePath, (log, result) =>
            {
                ProfileVersion subject = log.Logical(log.CursorVersion);
                ProfileVersion target = subject == null ? null : log.Find(subject.Parent);
                if (target == null)
                {
                    result.Message = "Nothing to undo.";
                    return null;
                }
                ProfileVersion version = WriteVersionLocked(log, profilePath, target, ProfileVersionKind.Undo, "Undo: " + subject.Title, subject);
                log.Redo.Add(subject.Id);
                result.Message = "Undone: " + subject.Description + ".";
                return version;
            });
        }

        public static ProfileHistoryWriteResult Redo(string profilePath)
        {
            return WriteBack(profilePath, (log, result) =>
            {
                ProfileVersion target = null;
                while (log.Redo.Count > 0 && target == null)
                {
                    target = log.Find(log.Redo[log.Redo.Count - 1]);
                    log.Redo.RemoveAt(log.Redo.Count - 1);
                }
                if (target == null)
                {
                    result.Message = "Nothing to redo.";
                    return null;
                }
                ProfileVersion version = WriteVersionLocked(log, profilePath, target, ProfileVersionKind.Redo, "Redo: " + target.Title, log.Logical(log.CursorVersion));
                result.Message = "Redone: " + target.Description + ".";
                return version;
            });
        }

        // Writes an older version back as a new version (undo returns to the state before the restore).
        public static ProfileHistoryWriteResult Restore(string profilePath, int versionId)
        {
            return WriteBack(profilePath, (log, result) =>
            {
                ProfileVersion target = log.Find(versionId);
                if (target == null)
                    throw new ArgumentException("version " + versionId.ToString(CultureInfo.InvariantCulture) + " is not in the history.");
                ProfileVersion current = log.CursorVersion;
                if (current != null && current.Hash == target.Hash)
                {
                    result.Message = "The profile is already at this version.";
                    return null;
                }
                string label = "Restored the version of " + FormatLocal(target.Utc) + " (" + target.Title + ")";
                ProfileVersion version = WriteVersionLocked(log, profilePath, target, ProfileVersionKind.Restore, label, log.Logical(current));
                log.Redo.Clear();
                result.Message = "Restored the version of " + FormatLocal(target.Utc) + ".";
                return version;
            });
        }

        public static bool SetKeep(string profilePath, int versionId, bool keep)
        {
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
                return false;
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                ProfileVersion version = log.Find(versionId);
                if (version == null)
                    return false;
                version.Keep = keep;
                SaveLog(log);
                return true;
            }
        }

        // The profile file was renamed or moved by JoyPro: its history goes along.
        public static bool Move(string oldProfilePath, string newProfilePath)
        {
            string from = FolderFor(oldProfilePath);
            string to = FolderFor(newProfilePath);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from) || Directory.Exists(to))
                return false;
            try
            {
                Directory.Move(from, to);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            using (FolderLock.Take(to))
            {
                ProfileHistoryLog log = LoadLog(to);
                log.ProfilePath = NormalizePath(newProfilePath);
                SaveLog(log);
            }
            return true;
        }

        // ---- clean-up ---------------------------------------------------------------------------------------------

        public static ProfileHistoryPruneResult Prune(string profilePath, ProfileHistoryPolicy policy = null, bool dryRun = false)
        {
            policy = policy ?? ProfileHistoryPolicy.Default;
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
                return new ProfileHistoryPruneResult { DryRun = dryRun };
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                ProfileHistoryPruneResult result = PruneLocked(log, policy, dryRun);
                if (!dryRun)
                    SaveLog(log);
                return result;
            }
        }

        // Every profile's history, then the global cap (the oldest versions of all profiles go first).
        public static ProfileHistoryPruneResult PruneAll(ProfileHistoryPolicy policy = null, bool dryRun = false)
        {
            policy = policy ?? ProfileHistoryPolicy.Default;
            ProfileHistoryPruneResult total = new ProfileHistoryPruneResult { DryRun = dryRun };
            List<string> folders = HistoryFolders().ToList();
            foreach (string folder in folders)
            {
                using (FolderLock.Take(folder))
                {
                    ProfileHistoryLog log = LoadLog(folder);
                    ProfileHistoryPruneResult one = PruneLocked(log, policy, dryRun);
                    if (!dryRun)
                        SaveLog(log);
                    total.Removed.AddRange(one.Removed);
                    total.BytesBefore += one.BytesBefore;
                    total.BytesAfter += one.BytesAfter;
                    total.VersionsLeft += one.VersionsLeft;
                }
            }
            if (dryRun || total.BytesAfter <= policy.GlobalCapBytes)
                return total;

            // Over the global cap: the oldest removable version of all profiles goes, one at a time.
            Dictionary<string, long> bytesByFolder = folders.ToDictionary(f => f, FolderBytes, StringComparer.OrdinalIgnoreCase);
            long sum = bytesByFolder.Values.Sum();
            for (int guard = 0; sum > policy.GlobalCapBytes && guard < 100000; guard++)
            {
                string oldestFolder = null;
                ProfileVersion oldest = null;
                foreach (string folder in folders)
                {
                    ProfileHistoryLog log = LoadLog(folder);
                    HashSet<int> protectedIds = ProtectedIds(log);
                    ProfileVersion candidate = log.Versions.Where(v => !protectedIds.Contains(v.Id)).OrderBy(v => v.Utc).FirstOrDefault();
                    if (candidate != null && (oldest == null || candidate.Utc < oldest.Utc))
                    {
                        oldest = candidate;
                        oldestFolder = folder;
                    }
                }
                if (oldest == null)
                    break;
                using (FolderLock.Take(oldestFolder))
                {
                    ProfileHistoryLog log = LoadLog(oldestFolder);
                    ProfileVersion victim = log.Find(oldest.Id);
                    if (victim != null)
                    {
                        RemoveVersionsLocked(log, new HashSet<int> { victim.Id });
                        DeleteUnreferencedBlobs(log);
                        SaveLog(log);
                        total.Removed.Add(victim);
                        total.VersionsLeft--;
                    }
                }
                long after = FolderBytes(oldestFolder);
                sum += after - bytesByFolder[oldestFolder];
                bytesByFolder[oldestFolder] = after;
            }
            total.BytesAfter = sum;
            return total;
        }

        // ---- drafts (GUI state that is not in the profile yet) -----------------------------------------------------

        public static string ReadDrafts(string profilePath)
        {
            try
            {
                string file = Path.Combine(FolderFor(profilePath), DraftsFile);
                return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        public static void WriteDrafts(string profilePath, string json)
        {
            string folder = FolderFor(profilePath);
            string file = Path.Combine(folder, DraftsFile);
            if (string.IsNullOrWhiteSpace(json))
            {
                if (File.Exists(file))
                    File.Delete(file);
                return;
            }
            Directory.CreateDirectory(folder);
            WriteFileAtomic(file, Encoding.UTF8.GetBytes(json), false);
        }

        // ---- atomic file writes -----------------------------------------------------------------------------------

        // Writes the whole file or nothing: a temp file next to it, then swapped in. A crash leaves either the old or the
        // new file, never a half-written one. durable: the temp file is flushed to the disk before the swap (the profile
        // itself; also a power cut keeps it), the history's own files skip that (it costs up to half a second on a busy
        // disk, and a damaged copy is detected by its hash).
        public static void WriteFileAtomic(string path, byte[] content, bool durable = true)
        {
            string full = NormalizePath(path);
            string directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            string temp = full + ".tmp-" + ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                {
                    stream.Write(content ?? new byte[0], 0, content == null ? 0 : content.Length);
                    stream.Flush(durable);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(full))
                            File.Replace(temp, full, null, true);
                        else
                            File.Move(temp, full);
                        break;
                    }
                    catch (IOException) when (attempt < 40)
                    {
                        // Another program (a sync client, a virus scanner) holds the file for a moment.
                        Thread.Sleep(50);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 40)
                    {
                        Thread.Sleep(50);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // A file system without replace (some network shares): copy over, the temp file stays until then.
                        File.Copy(temp, full, true);
                        break;
                    }
                }
            }
            finally
            {
                TryDeleteFile(temp);
            }
            DeleteStaleTempFiles(full);
        }

        // ---- what changed -----------------------------------------------------------------------------------------

        // Items are lines "<kind>\t<name>\t<count>\t<detail>\t<hash>[\t<hash>...]": R relation (count = actions, detail =
        // groups; hashes: actions, kind + groups), B bind (detail = device and input; hashes: input, modifiers,
        // tuning), M modifier, S settings section.
        public static List<string> DescribeChanges(IList<string> before, IList<string> after)
        {
            Dictionary<string, Item> old = ParseItems(before);
            Dictionary<string, Item> now = ParseItems(after);
            List<string> lines = new List<string>();

            List<Item> addedRelations = now.Values.Where(i => i.Kind == "R" && !old.ContainsKey(i.Key)).ToList();
            List<Item> removedRelations = old.Values.Where(i => i.Kind == "R" && !now.ContainsKey(i.Key)).ToList();
            HashSet<string> renamedFrom = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> renamedTo = new HashSet<string>(StringComparer.Ordinal);
            foreach (Item removed in removedRelations)
            {
                Item match = addedRelations.FirstOrDefault(a => !renamedTo.Contains(a.Key) && a.Hash(0) == removed.Hash(0) && a.Hash(0).Length > 0);
                if (match == null)
                    continue;
                renamedFrom.Add(removed.Key);
                renamedTo.Add(match.Key);
                lines.Add("Relation '" + removed.Name + "' renamed to '" + match.Name + "'");
            }
            foreach (Item added in addedRelations.Where(a => !renamedTo.Contains(a.Key)))
                lines.Add("Relation '" + added.Name + "' created" + (added.Count > 0 ? ", " + Plural(added.Count, "action") + " added" : string.Empty));
            foreach (Item removed in removedRelations.Where(r => !renamedFrom.Contains(r.Key)))
                lines.Add("Relation '" + removed.Name + "' deleted" + (removed.Count > 0 ? " (" + Plural(removed.Count, "action") + ")" : string.Empty));
            foreach (Item item in now.Values.Where(i => i.Kind == "R" && old.ContainsKey(i.Key)))
            {
                Item was = old[item.Key];
                if (item.Hash(0) != was.Hash(0))
                {
                    int delta = item.Count - was.Count;
                    lines.Add("Relation '" + item.Name + "': " + (delta > 0 ? Plural(delta, "action") + " added" : delta < 0 ? Plural(-delta, "action") + " removed" : "actions changed"));
                }
                if (item.Hash(1) != was.Hash(1))
                {
                    lines.Add(item.Detail != was.Detail
                        ? "Relation '" + item.Name + "': groups " + (was.Detail.Length == 0 ? "(none)" : was.Detail) + " -> " + (item.Detail.Length == 0 ? "(none)" : item.Detail)
                        : "Relation '" + item.Name + "' changed");
                }
            }

            foreach (Item item in now.Values.Where(i => i.Kind == "B"))
            {
                Item was;
                if (!old.TryGetValue(item.Key, out was) || was.Hash(0).Length == 0)
                {
                    if (item.Hash(0).Length > 0)
                        lines.Add("'" + item.Name + "' bound to " + item.Detail);
                    continue;
                }
                if (item.Hash(0).Length == 0)
                {
                    if (!renamedFrom.Contains("R\t" + item.Name))
                        lines.Add("'" + item.Name + "' unbound");
                    continue;
                }
                if (item.Hash(0) != was.Hash(0))
                    lines.Add("'" + item.Name + "' moved from " + was.Detail + " to " + item.Detail);
                if (item.Hash(1) != was.Hash(1))
                    lines.Add("'" + item.Name + "': modifiers changed");
                if (item.Hash(2) != was.Hash(2))
                    lines.Add("'" + item.Name + "': axis tuning changed");
            }
            foreach (Item was in old.Values.Where(i => i.Kind == "B" && !now.ContainsKey(i.Key) && i.Hash(0).Length > 0))
            {
                if (!old.ContainsKey("R\t" + was.Name) || now.ContainsKey("R\t" + was.Name))
                    lines.Add("'" + was.Name + "' unbound");
            }

            foreach (Item item in now.Values.Where(i => i.Kind == "M"))
            {
                Item was;
                if (!old.TryGetValue(item.Key, out was))
                    lines.Add("Modifier '" + item.Name + "' created" + (item.Detail.Length > 0 ? " on " + item.Detail : string.Empty));
                else if (item.Hash(0) != was.Hash(0))
                    lines.Add("Modifier '" + item.Name + "' changed" + (item.Detail.Length > 0 ? " (" + item.Detail + ")" : string.Empty));
            }
            foreach (Item was in old.Values.Where(i => i.Kind == "M" && !now.ContainsKey(i.Key)))
                lines.Add("Modifier '" + was.Name + "' deleted");

            foreach (Item item in now.Values.Where(i => i.Kind == "S"))
            {
                Item was;
                if (!old.TryGetValue(item.Key, out was) || item.Hash(0) != was.Hash(0))
                    lines.Add(item.Name + " changed");
            }
            foreach (Item was in old.Values.Where(i => i.Kind == "S" && !now.ContainsKey(i.Key)))
                lines.Add(was.Name + " cleared");
            return lines;
        }

        // One short line for the history list: the changes themselves when there are few, else counts.
        public static string Summarize(IList<string> changes)
        {
            if (changes == null)
                return string.Empty;
            if (changes.Count == 0)
                return "No change in relations, binds or settings";
            if (changes.Count <= 2)
                return string.Join("; ", changes);
            int created = changes.Count(c => c.StartsWith("Relation '", StringComparison.Ordinal) && c.Contains("' created"));
            int deleted = changes.Count(c => c.StartsWith("Relation '", StringComparison.Ordinal) && c.Contains("' deleted"));
            int renamed = changes.Count(c => c.StartsWith("Relation '", StringComparison.Ordinal) && c.Contains("' renamed to '"));
            int relationChanged = changes.Count(c => c.StartsWith("Relation '", StringComparison.Ordinal)) - created - deleted - renamed;
            int bound = changes.Count(c => c.Contains("' bound to "));
            int unbound = changes.Count(c => c.EndsWith("' unbound", StringComparison.Ordinal));
            int bindChanged = changes.Count(c => c.StartsWith("'", StringComparison.Ordinal)) - bound - unbound;
            int modifiers = changes.Count(c => c.StartsWith("Modifier '", StringComparison.Ordinal));
            int settings = changes.Count - created - deleted - renamed - relationChanged - bound - unbound - bindChanged - modifiers;
            List<string> parts = new List<string>();
            if (created > 0) parts.Add(Plural(created, "relation") + " created");
            if (deleted > 0) parts.Add(Plural(deleted, "relation") + " deleted");
            if (renamed > 0) parts.Add(Plural(renamed, "relation") + " renamed");
            if (relationChanged > 0) parts.Add(Plural(relationChanged, "relation change"));
            if (bound > 0) parts.Add(Plural(bound, "bind") + " set");
            if (unbound > 0) parts.Add(Plural(unbound, "bind") + " removed");
            if (bindChanged > 0) parts.Add(Plural(bindChanged, "bind change"));
            if (modifiers > 0) parts.Add(Plural(modifiers, "modifier change"));
            if (settings > 0) parts.Add(Plural(settings, "setting change"));
            return string.Join(", ", parts);
        }

        // "450 relations, 380 binds" for a version without a predecessor.
        public static string DescribeContent(IList<string> items)
        {
            if (items == null)
                return string.Empty;
            int relations = items.Count(i => i.StartsWith("R\t", StringComparison.Ordinal));
            int binds = items.Count(i =>
            {
                if (!i.StartsWith("B\t", StringComparison.Ordinal))
                    return false;
                string[] parts = i.Split('\t');
                return parts.Length > 4 && parts[4].Length > 0;
            });
            int modifiers = items.Count(i => i.StartsWith("M\t", StringComparison.Ordinal));
            string text = Plural(relations, "relation") + ", " + Plural(binds, "bind");
            return modifiers > 0 ? text + ", " + Plural(modifiers, "modifier") : text;
        }

        public static string FormatLocal(DateTime utc)
        {
            DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024)
                return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        // ---- internals --------------------------------------------------------------------------------------------

        private static ProfileVersion RecordLocked(ProfileHistoryLog log, byte[] content, ProfileHistoryRecordOptions options, ProfileHistoryPolicy policy)
        {
            string kind = string.IsNullOrWhiteSpace(options.Kind) ? ProfileVersionKind.Change : options.Kind;
            string hash = HashOf(content);
            DateTime now = UtcNow();
            ProfileVersion latest = log.Latest;
            ProfileVersion cursor = log.CursorVersion;
            string label = (options.Label ?? string.Empty).Trim();

            // Saved without a change: the profile stays at its version.
            if ((kind == ProfileVersionKind.Change || kind == ProfileVersionKind.External || kind == ProfileVersionKind.Open) && cursor != null && cursor.Hash == hash)
                return cursor;
            if (kind == ProfileVersionKind.Checkpoint && latest != null && latest.Kind == ProfileVersionKind.Checkpoint && latest.Hash == hash &&
                string.Equals(latest.Label, label, StringComparison.Ordinal) && Math.Abs((now - latest.Utc).TotalSeconds) < 60)
                return latest;

            EnsureBlob(log.Folder, hash, content);
            EnsureItems(log.Folder, hash, options.Items);

            ProfileVersion parent = log.Logical(cursor);
            if (kind == ProfileVersionKind.Change && options.Coalesce && latest != null && latest == cursor && latest.Kind == ProfileVersionKind.Change &&
                !latest.Keep && latest.Parent > 0 && (now - latest.Utc).TotalSeconds >= 0 && (now - latest.Utc).TotalSeconds < policy.CoalesceSeconds)
            {
                // One click, several saves: one version, made on the version before the first save.
                log.Versions.Remove(latest);
                parent = log.Find(latest.Parent);
                label = MergeLabels(latest.Label, label);
                if (!log.Versions.Any(v => v.Hash == latest.Hash))
                    DeleteBlob(log.Folder, latest.Hash);
                if (parent != null && parent.Hash == hash)
                {
                    // The burst undid itself: back at the parent.
                    log.Cursor = parent.Id;
                    return parent;
                }
            }

            ProfileVersion version = new ProfileVersion
            {
                Id = log.NextId++,
                Utc = now,
                Kind = kind,
                Label = label,
                Hash = hash,
                Size = content.Length,
                Parent = parent == null ? 0 : parent.Id,
                From = options.From
            };
            if (options.Summary != null)
            {
                version.Summary = options.Summary;
            }
            else if (parent != null && parent.Hash != hash)
            {
                IList<string> before = ReadItemsOf(log.Folder, parent.Hash);
                IList<string> after = ReadItemsOf(log.Folder, hash);
                version.Summary = before != null && after != null ? Summarize(DescribeChanges(before, after)) : string.Empty;
            }
            else if (parent == null)
            {
                version.Summary = DescribeContent(ReadItemsOf(log.Folder, hash));
            }
            log.Versions.Add(version);

            // A checkpoint of the content the profile is at does not move the undo cursor.
            bool sameContent = cursor != null && cursor.Hash == hash;
            if (!(kind == ProfileVersionKind.Checkpoint && sameContent))
            {
                log.Cursor = version.Id;
                if (kind != ProfileVersionKind.Undo && kind != ProfileVersionKind.Redo)
                    log.Redo.Clear();
            }
            return version;
        }

        private static ProfileVersion RecordOutsideChangeLocked(ProfileHistoryLog log, string profilePath, Func<IList<string>> items)
        {
            if (!File.Exists(profilePath))
                return log.CursorVersion;
            byte[] content = File.ReadAllBytes(profilePath);
            ProfileVersion cursor = log.CursorVersion;
            if (cursor != null && cursor.Hash == HashOf(content))
                return cursor;
            return RecordLocked(log, content, new ProfileHistoryRecordOptions
            {
                Kind = cursor == null ? ProfileVersionKind.Open : ProfileVersionKind.External,
                Label = cursor == null ? "First version" : "Changed outside JoyPro",
                Items = items,
                Coalesce = false
            }, ProfileHistoryPolicy.Default);
        }

        private static ProfileHistoryWriteResult WriteBack(string profilePath, Func<ProfileHistoryLog, ProfileHistoryWriteResult, ProfileVersion> action)
        {
            ProfileHistoryWriteResult result = new ProfileHistoryWriteResult();
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                result.Message = "No profile is open.";
                return result;
            }
            string folder = FolderFor(profilePath);
            if (!File.Exists(Path.Combine(folder, IndexFile)))
            {
                result.Message = "This profile has no history yet.";
                return result;
            }
            using (FolderLock.Take(folder))
            {
                ProfileHistoryLog log = LoadLog(folder);
                log.ProfilePath = NormalizePath(profilePath);
                RecordOutsideChangeLocked(log, profilePath, null);
                ProfileVersion version = action(log, result);
                SaveLog(log);
                if (version != null)
                {
                    result.Changed = true;
                    result.Version = version;
                    result.Target = log.Find(version.From) ?? version;
                }
                return result;
            }
        }

        // Writes the target's content into the profile file and records it as a version of the given kind.
        private static ProfileVersion WriteVersionLocked(ProfileHistoryLog log, string profilePath, ProfileVersion target, string kind, string label, ProfileVersion comparedTo)
        {
            byte[] content = ReadBlob(log.Folder, target.Hash);
            WriteFileAtomic(profilePath, content);
            string summary = string.Empty;
            if (comparedTo != null && comparedTo.Hash != target.Hash)
            {
                IList<string> before = ReadItemsOf(log.Folder, comparedTo.Hash);
                IList<string> after = ReadItemsOf(log.Folder, target.Hash);
                if (before != null && after != null)
                    summary = Summarize(DescribeChanges(before, after));
            }
            ProfileVersion version = new ProfileVersion
            {
                Id = log.NextId++,
                Utc = UtcNow(),
                Kind = kind,
                Label = label,
                Summary = summary,
                Hash = target.Hash,
                Size = target.Size,
                Parent = comparedTo == null ? 0 : comparedTo.Id,
                From = target.Id
            };
            log.Versions.Add(version);
            log.Cursor = version.Id;
            return version;
        }

        private static HashSet<int> ProtectedIds(ProfileHistoryLog log)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (ProfileVersion version in log.Versions)
            {
                if (version.Keep)
                    ids.Add(version.Id);
            }
            if (log.Latest != null)
                ids.Add(log.Latest.Id);
            ProfileVersion cursor = log.CursorVersion;
            if (cursor != null)
            {
                ids.Add(cursor.Id);
                ProfileVersion logical = log.Logical(cursor);
                if (logical != null)
                    ids.Add(logical.Id);
            }
            foreach (int id in log.Redo)
                ids.Add(id);
            return ids;
        }

        private static ProfileHistoryPruneResult PruneLocked(ProfileHistoryLog log, ProfileHistoryPolicy policy, bool dryRun)
        {
            ProfileHistoryPruneResult result = new ProfileHistoryPruneResult { DryRun = dryRun };
            DateTime now = UtcNow();
            HashSet<int> protectedIds = ProtectedIds(log);
            Dictionary<string, long> blobBytes = new Dictionary<string, long>(StringComparer.Ordinal);
            Func<string, long> bytesOf = hash =>
            {
                long size;
                if (!blobBytes.TryGetValue(hash, out size))
                {
                    size = FileLength(BlobPath(log.Folder, hash)) + FileLength(ItemsPath(log.Folder, hash));
                    blobBytes[hash] = size;
                }
                return size;
            };
            long indexBytes = FileLength(Path.Combine(log.Folder, IndexFile));
            Func<IEnumerable<ProfileVersion>, long> storedBytes = versions =>
                indexBytes + versions.Select(v => v.Hash).Distinct(StringComparer.Ordinal).Sum(bytesOf);
            result.BytesBefore = storedBytes(log.Versions);

            // Retention: everything of the last day, then one version per hour, day and week.
            HashSet<int> remove = new HashSet<int>();
            HashSet<string> buckets = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProfileVersion version in log.Versions.OrderByDescending(v => v.Utc).ThenByDescending(v => v.Id))
            {
                if (protectedIds.Contains(version.Id))
                    continue;
                TimeSpan age = now - version.Utc;
                if (age.TotalHours < policy.KeepEverythingHours)
                    continue;
                string bucket;
                if (age.TotalDays < policy.HourlyDays)
                    bucket = "h" + version.Utc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                else if (age.TotalDays < policy.DailyDays)
                    bucket = "d" + version.Utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                else
                    bucket = "w" + version.Utc.Date.AddDays(-(((int)version.Utc.DayOfWeek + 6) % 7)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!buckets.Add(bucket))
                    remove.Add(version.Id);
            }

            // Size cap: the oldest versions go first.
            List<ProfileVersion> survivors = log.Versions.Where(v => !remove.Contains(v.Id)).ToList();
            foreach (ProfileVersion version in log.Versions.OrderBy(v => v.Utc).ThenBy(v => v.Id))
            {
                if (storedBytes(survivors) <= policy.ProfileCapBytes)
                    break;
                if (protectedIds.Contains(version.Id) || remove.Contains(version.Id))
                    continue;
                remove.Add(version.Id);
                survivors.Remove(version);
            }

            result.Removed.AddRange(log.Versions.Where(v => remove.Contains(v.Id)));
            result.VersionsLeft = survivors.Count;
            result.BytesAfter = storedBytes(survivors);
            if (dryRun)
                return result;

            RemoveVersionsLocked(log, remove);
            DeleteUnreferencedBlobs(log);
            log.LastPruneUtc = now;
            result.BytesAfter = FolderBytes(log.Folder);
            return result;
        }

        private static void RemoveVersionsLocked(ProfileHistoryLog log, HashSet<int> remove)
        {
            if (remove.Count == 0)
                return;
            Dictionary<int, ProfileVersion> removed = log.Versions.Where(v => remove.Contains(v.Id)).ToDictionary(v => v.Id);
            log.Versions.RemoveAll(v => remove.Contains(v.Id));
            // Undo skips what was cleaned up: a parent that is gone is replaced by its own parent.
            foreach (ProfileVersion version in log.Versions)
            {
                for (int guard = 0; version.Parent > 0 && removed.ContainsKey(version.Parent) && guard < 100000; guard++)
                    version.Parent = removed[version.Parent].Parent;
            }
            log.Redo.RemoveAll(id => remove.Contains(id));
        }

        private static void DeleteUnreferencedBlobs(ProfileHistoryLog log)
        {
            string folder = Path.Combine(log.Folder, BlobFolder);
            if (!Directory.Exists(folder))
                return;
            HashSet<string> used = new HashSet<string>(log.Versions.Select(v => v.Hash), StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                int dot = name.IndexOf('.');
                string hash = dot < 0 ? name : name.Substring(0, dot);
                bool temp = name.IndexOf(".tmp-", StringComparison.OrdinalIgnoreCase) >= 0;
                if (temp ? IsOlderThanMinutes(file, 10) : !used.Contains(hash))
                    TryDeleteFile(file);
            }
        }

        private static void DeleteBlob(string folder, string hash)
        {
            TryDeleteFile(BlobPath(folder, hash));
            TryDeleteFile(ItemsPath(folder, hash));
        }

        private static string BlobPath(string folder, string hash)
        {
            return Path.Combine(folder, BlobFolder, hash + ".gz");
        }

        private static string ItemsPath(string folder, string hash)
        {
            return Path.Combine(folder, BlobFolder, hash + ".items.gz");
        }

        private static void EnsureBlob(string folder, string hash, byte[] content)
        {
            string path = BlobPath(folder, hash);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;
            WriteFileAtomic(path, Compress(content), false);
        }

        private static void EnsureItems(string folder, string hash, Func<IList<string>> items)
        {
            if (items == null)
                return;
            string path = ItemsPath(folder, hash);
            if (File.Exists(path))
                return;
            IList<string> lines;
            try
            {
                lines = items();
            }
            catch (Exception)
            {
                // Items only describe the change; the version itself counts.
                return;
            }
            if (lines == null)
                return;
            // A header line: an empty profile still gives a non-empty file.
            WriteFileAtomic(path, Compress(Encoding.UTF8.GetBytes(ItemsHeader + "\n" + string.Join("\n", lines))), false);
        }

        private static byte[] ReadBlob(string folder, string hash)
        {
            string path = BlobPath(folder, hash);
            if (!File.Exists(path))
                throw new FileNotFoundException("The stored copy of this version is missing.", path);
            byte[] content = Decompress(File.ReadAllBytes(path));
            if (HashOf(content) != hash)
                throw new InvalidDataException("The stored copy of this version is damaged.");
            return content;
        }

        public static IList<string> ReadItemsOf(string folder, string hash)
        {
            string path = ItemsPath(folder, hash);
            if (!File.Exists(path))
                return null;
            try
            {
                string text = Encoding.UTF8.GetString(Decompress(File.ReadAllBytes(path)));
                return text.Split('\n').Where(line => line.Length > 0 && line[0] != '#').ToList();
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static byte[] Compress(byte[] content)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                    gzip.Write(content, 0, content.Length);
                return output.ToArray();
            }
        }

        private static byte[] Decompress(byte[] content)
        {
            using (MemoryStream input = new MemoryStream(content))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        // A profile file moved or renamed outside JoyPro: a history whose newest version is this content and whose
        // profile file is gone is the same profile.
        private static void TryAdopt(string profilePath, string folder, string previousHash)
        {
            if (string.IsNullOrWhiteSpace(previousHash) || Directory.Exists(folder))
                return;
            try
            {
                foreach (string candidate in HistoryFolders())
                {
                    ProfileHistoryLog log = LoadLog(candidate);
                    ProfileVersion latest = log.Latest;
                    if (latest == null || latest.Hash != previousHash || string.IsNullOrWhiteSpace(log.ProfilePath) || File.Exists(log.ProfilePath))
                        continue;
                    Directory.Move(candidate, folder);
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static IEnumerable<string> HistoryFolders()
        {
            string root = Root();
            if (!Directory.Exists(root))
                return new string[0];
            return Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, IndexFile))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ProfileHistoryLog LoadLog(string folder)
        {
            ProfileHistoryLog log = new ProfileHistoryLog { Folder = folder };
            string file = Path.Combine(folder, IndexFile);
            if (!File.Exists(file))
                return log;
            string json = null;
            for (int attempt = 0; attempt < 20 && json == null; attempt++)
            {
                try
                {
                    json = File.ReadAllText(file, Encoding.UTF8);
                }
                catch (IOException)
                {
                    Thread.Sleep(25);
                }
            }
            Dictionary<string, object> root;
            try
            {
                root = json == null ? null : MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                root = null;
            }
            if (root == null)
                return log;
            log.ProfilePath = Str(root, "profilePath");
            log.Cursor = Int(root, "cursor");
            log.NextId = Math.Max(1, Int(root, "nextId"));
            log.LastPruneUtc = Date(root, "lastPruneUtc");
            List<object> redo = Get(root, "redo") as List<object>;
            if (redo != null)
                log.Redo = redo.Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture)).ToList();
            List<object> versions = Get(root, "versions") as List<object>;
            if (versions != null)
            {
                foreach (Dictionary<string, object> entry in versions.OfType<Dictionary<string, object>>())
                {
                    log.Versions.Add(new ProfileVersion
                    {
                        Id = Int(entry, "id"),
                        Utc = Date(entry, "utc"),
                        Kind = Str(entry, "kind").Length == 0 ? ProfileVersionKind.Change : Str(entry, "kind"),
                        Label = Str(entry, "label"),
                        Summary = Str(entry, "summary"),
                        Hash = Str(entry, "hash"),
                        Size = (long)Num(entry, "size"),
                        Keep = Get(entry, "keep") is bool && (bool)Get(entry, "keep"),
                        Parent = Int(entry, "parent"),
                        From = Int(entry, "from")
                    });
                }
            }
            int maxId = log.Versions.Count == 0 ? 0 : log.Versions.Max(v => v.Id);
            if (log.NextId <= maxId)
                log.NextId = maxId + 1;
            return log;
        }

        private static void SaveLog(ProfileHistoryLog log)
        {
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "format", 1 },
                { "profilePath", log.ProfilePath ?? string.Empty },
                { "nextId", log.NextId },
                { "cursor", log.Cursor },
                { "redo", log.Redo.Cast<object>().ToList() },
                { "lastPruneUtc", log.LastPruneUtc == DateTime.MinValue ? string.Empty : log.LastPruneUtc.ToString("o", CultureInfo.InvariantCulture) },
                {
                    "versions", log.Versions.Select(v => (object)new Dictionary<string, object>
                    {
                        { "id", v.Id },
                        { "utc", v.Utc.ToString("o", CultureInfo.InvariantCulture) },
                        { "kind", v.Kind ?? ProfileVersionKind.Change },
                        { "label", v.Label ?? string.Empty },
                        { "summary", v.Summary ?? string.Empty },
                        { "hash", v.Hash ?? string.Empty },
                        { "size", v.Size },
                        { "keep", v.Keep },
                        { "parent", v.Parent },
                        { "from", v.From }
                    }).ToList()
                }
            };
            Directory.CreateDirectory(log.Folder);
            WriteFileAtomic(Path.Combine(log.Folder, IndexFile), Encoding.UTF8.GetBytes(MiniJson.Write(root, true)), false);
        }

        private static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        private static string Str(Dictionary<string, object> map, string key)
        {
            return Get(map, key) as string ?? string.Empty;
        }

        private static double Num(Dictionary<string, object> map, string key)
        {
            object value = Get(map, key);
            return value is double ? (double)value : 0;
        }

        private static int Int(Dictionary<string, object> map, string key)
        {
            return (int)Num(map, key);
        }

        private static DateTime Date(Dictionary<string, object> map, string key)
        {
            DateTime value;
            return DateTime.TryParse(Str(map, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)
                ? (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime())
                : DateTime.MinValue;
        }

        private static string MergeLabels(string first, string second)
        {
            first = (first ?? string.Empty).Trim();
            second = (second ?? string.Empty).Trim();
            if (first.Length == 0)
                return second;
            if (second.Length == 0 || string.Equals(first, second, StringComparison.OrdinalIgnoreCase) || first.Split(new[] { " + " }, StringSplitOptions.None).Contains(second))
                return first;
            return first + " + " + second;
        }

        private static string Plural(int count, string noun)
        {
            return count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
        }

        private sealed class Item
        {
            public string Kind = string.Empty;
            public string Name = string.Empty;
            public int Count;
            public string Detail = string.Empty;
            public string[] Hashes = new string[0];

            public string Key
            {
                get { return Kind + "\t" + Name; }
            }

            public string Hash(int index)
            {
                return index < Hashes.Length ? Hashes[index] : string.Empty;
            }
        }

        private static Dictionary<string, Item> ParseItems(IList<string> lines)
        {
            Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.Ordinal);
            if (lines == null)
                return items;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 2)
                    continue;
                int count;
                Item item = new Item
                {
                    Kind = parts[0],
                    Name = parts[1],
                    Count = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ? count : 0,
                    Detail = parts.Length > 3 ? parts[3] : string.Empty,
                    Hashes = parts.Length > 4 ? parts.Skip(4).ToArray() : new string[0]
                };
                items[item.Key] = item;
            }
            return items;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath((path ?? string.Empty).Trim());
            }
            catch (Exception)
            {
                return (path ?? string.Empty).Trim();
            }
        }

        private static long FileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        private static long FolderBytes(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static bool IsOlderThanMinutes(string file, int minutes)
        {
            try
            {
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMinutes > minutes;
            }
            catch (IOException)
            {
                return false;
            }
        }

        // Temp files a crash left next to the file.
        private static void DeleteStaleTempFiles(string full)
        {
            try
            {
                string directory = Path.GetDirectoryName(full);
                string prefix = Path.GetFileName(full) + ".tmp-";
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    return;
                foreach (string file in Directory.GetFiles(directory, prefix + "*"))
                {
                    if (IsOlderThanMinutes(file, 10))
                        TryDeleteFile(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static readonly int ProcessId = CurrentProcessId();

        private static int CurrentProcessId()
        {
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                return process.Id;
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // The folder's .lock file, held open without sharing; other processes wait for it.
        private sealed class FolderLock : IDisposable
        {
            private FileStream stream;

            public static FolderLock Take(string folder)
            {
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, LockFile);
                DateTime until = DateTime.UtcNow.AddSeconds(20);
                while (true)
                {
                    try
                    {
                        return new FolderLock { stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) };
                    }
                    catch (IOException)
                    {
                        if (DateTime.UtcNow > until)
                            throw;
                        Thread.Sleep(25);
                    }
                }
            }

            public void Dispose()
            {
                if (stream != null)
                    stream.Dispose();
                stream = null;
            }
        }
    }
}
