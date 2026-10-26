// JoyPro2's one local data folder (docs/installs.md "Data folder"): C# 7.3, compiled into the engine and into
// JoyPro.Logging (GUI, tray host, watcher, force feedback process), so every process resolves the same folder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JoyPro.Logging
{
    // <data> = JOYPRO_DATA_DIR when set (tests, portable setups: nothing under %LOCALAPPDATA% is read, written or
    // moved then), else %LOCALAPPDATA%\JoyPro2.
    //
    // Up to 2.3.x JoyPro2 used two folders: %LOCALAPPDATA%\JoyPro (game data, DB, profile history, backups, usage, ...)
    // and %LOCALAPPDATA%\JoyPro2 (logs, crash reports, pictures, updates). The first process of a newer JoyPro2 that
    // needs the data folder moves the old folder's content into JoyPro2 (Migrate):
    //  - both folders are siblings on one drive: every move is a rename, nothing is copied and deleted halfway; a
    //    process that stops in the middle leaves each file either in the old or in the new place, and the next start
    //    goes on where it stopped (the move is the same every time: whatever is still in the old folder moves);
    //  - a file in use (an old JoyPro2 still running, a log open) stays where it is; the old folder then gets a note
    //    (MOVED-TO-JoyPro2.txt) and the next start moves the rest;
    //  - a file that is in both folders: the same content goes once; different content: JoyPro2's copy stays and the
    //    old one is kept under <data>\MigrationConflicts\ (nothing is ever deleted that is not in the new place);
    //  - only when the old folder is empty, it is removed, so only JoyPro2 is left (2.4.0 put a junction
    //    %LOCALAPPDATA%\JoyPro -> JoyPro2 in its place, which looked like a second data folder: the next start removes
    //    that junction as a link, only when it points to JoyPro2; once the old folder is gone a start checks only that
    //    it does not exist and logs nothing);
    //  - a copy is made (and checked: size and SHA-256) only when a rename is impossible (another drive).
    // <data>\data-migration.json records what happened (shown in the support file's environment).
    public static class JoyProDataFolder
    {
        public const string Variable = "JOYPRO_DATA_DIR";
        public const string FolderName = "JoyPro2";
        public const string LegacyFolderName = "JoyPro";
        public const string StateFileName = "data-migration.json";
        public const string MovedNoteName = "MOVED-TO-JoyPro2.txt";
        public const string ConflictFolderName = "MigrationConflicts";
        private const string CopySuffix = ".joypro-moving";

        // Tests: stands in for %LOCALAPPDATA% (JOYPRO_LOCALAPPDATA does the same for a process the smoke test starts).
        public static string LocalAppDataOverride;
        public const string LocalAppDataVariable = "JOYPRO_LOCALAPPDATA";

        // JOYPRO_NO_DATA_MIGRATION=1 (the GUI tests): nothing is moved, whatever the other variables say.
        public const string NoMigrationVariable = "JOYPRO_NO_DATA_MIGRATION";

        private static readonly object Gate = new object();
        private static string checkedFor;

        // What this process's EnsureMigrated did (null: nothing checked); JoyProLog.Start logs it.
        public static DataMigrationResult LastResult;

        public static string LocalAppData
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LocalAppDataOverride))
                    return LocalAppDataOverride;
                string variable = Environment.GetEnvironmentVariable(LocalAppDataVariable);
                return string.IsNullOrWhiteSpace(variable)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : variable.Trim();
            }
        }

        // JOYPRO_DATA_DIR is set: a portable setup or a test run with a data folder of its own.
        public static bool IsCustom
        {
            get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)); }
        }

        public static string DefaultRoot
        {
            get { return Path.Combine(LocalAppData, FolderName); }
        }

        public static string LegacyRoot
        {
            get { return Path.Combine(LocalAppData, LegacyFolderName); }
        }

        // The data folder; the first call of a process moves an old %LOCALAPPDATA%\JoyPro in (never with
        // JOYPRO_DATA_DIR). The folder itself is not created here.
        public static string Root()
        {
            string custom = Environment.GetEnvironmentVariable(Variable);
            if (!string.IsNullOrWhiteSpace(custom))
                return custom.Trim();
            EnsureMigrated();
            return DefaultRoot;
        }

        public static string Combine(string part)
        {
            return Path.Combine(Root(), part);
        }

        // Once per process (and per %LOCALAPPDATA%); a failure never stops JoyPro, the next start tries again.
        public static void EnsureMigrated()
        {
            if (Environment.GetEnvironmentVariable(NoMigrationVariable) == "1" || IsCustom)
                return;
            string local = LocalAppData;
            lock (Gate)
            {
                if (string.Equals(checkedFor, local, StringComparison.OrdinalIgnoreCase))
                    return;
                checkedFor = local;
            }
            try
            {
                LastResult = Migrate(local);
            }
            catch (Exception ex)
            {
                JoyProLog.Warn("Data", "Moving the old data folder failed; the next start tries again", ex);
            }
        }

        // Tests: the next Root() checks again.
        public static void ResetCheck()
        {
            lock (Gate)
                checkedFor = null;
        }

        public static bool IsJunction(string path)
        {
            try
            {
                return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Moves <local>\JoyPro into <local>\JoyPro2 (see the class comment). Safe to call any time and from several
        // processes (one at a time).
        public static DataMigrationResult Migrate(string localAppData)
        {
            DataMigrationResult result = new DataMigrationResult();
            string legacy = Path.Combine(localAppData, LegacyFolderName);
            string target = Path.Combine(localAppData, FolderName);
            result.From = legacy;
            result.To = target;
            // The usual start once the move is done: one check, nothing logged.
            if (!Directory.Exists(legacy))
                return result;
            if (IsJunction(legacy))
            {
                // 2.4.0 left a junction JoyPro -> JoyPro2 (it looked like a second data folder): removed as a link,
                // its files stay in JoyPro2. A link that points anywhere else is the user's and stays.
                if (PointsTo(legacy, target) && TryRemoveLink(legacy))
                {
                    result.Status = "done";
                    result.OldFolderRemoved = true;
                    WriteState(target, result.Status, result);
                    JoyProLog.Info("Data", Describe(result));
                }
                return result;
            }
            using (Mutex mutex = new Mutex(false, @"Local\JoyPro2.DataMigration." + ShortHash(target.ToUpperInvariant())))
            {
                bool owned;
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(60));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
                if (!owned)
                {
                    result.Status = "busy";
                    return result;
                }
                try
                {
                    if (!Directory.Exists(legacy) || IsJunction(legacy))
                    {
                        result.Status = "done";
                        return result;
                    }
                    Directory.CreateDirectory(target);
                    WriteState(target, "moving", result);
                    MoveContents(legacy, target, target, string.Empty, result);
                    string note = Path.Combine(legacy, MovedNoteName);
                    bool empty = !Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories)
                        .Any(f => !string.Equals(Path.GetFileName(f), MovedNoteName, StringComparison.OrdinalIgnoreCase));
                    if (empty && DeleteEmptyTree(legacy))
                    {
                        // Only JoyPro2 is left (no junction any more: it looked like a second data folder).
                        result.OldFolderRemoved = true;
                        result.Status = "done";
                    }
                    else
                    {
                        File.WriteAllText(note,
                            "JoyPro2 keeps all its data in " + target + " now." + Environment.NewLine +
                            "The files still here were in use when JoyPro2 moved this folder; the next start of JoyPro2 moves them." + Environment.NewLine,
                            Encoding.UTF8);
                        result.Status = "partial";
                    }
                    WriteState(target, result.Status, result);
                    JoyProLog.Info("Data", Describe(result));
                    return result;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static void MoveContents(string from, string to, string targetRoot, string relative, DataMigrationResult result)
        {
            foreach (string dir in SafeEntries(() => Directory.GetDirectories(from)))
            {
                string name = Path.GetFileName(dir);
                string destination = Path.Combine(to, name);
                string rel = relative.Length == 0 ? name : Path.Combine(relative, name);
                if (IsJunction(dir))
                {
                    // A link inside the old folder: never followed (its target is not JoyPro's), kept for the user.
                    result.Left.Add(rel);
                    continue;
                }
                if (!Directory.Exists(destination) && !File.Exists(destination) && SameVolume(dir, to))
                {
                    try
                    {
                        Directory.Move(dir, destination);
                        result.Moved++;
                        continue;
                    }
                    catch (IOException)
                    {
                        // A file in it is in use: file by file below.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                Directory.CreateDirectory(destination);
                MoveContents(dir, destination, targetRoot, rel, result);
                TryDeleteEmpty(dir);
            }
            foreach (string file in SafeEntries(() => Directory.GetFiles(from)))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, MovedNoteName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = relative.Length == 0 ? name : Path.Combine(relative, name);
                if (name.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // A copy left by a stop in the middle (the original is still there): not data.
                    TryDelete(file);
                    continue;
                }
                MoveFile(file, Path.Combine(to, name), targetRoot, rel, result);
            }
        }

        private static void MoveFile(string source, string destination, string targetRoot, string relative, DataMigrationResult result)
        {
            try
            {
                // A copy a stop in the middle left next to its place (the original is still here): not data.
                if (File.Exists(destination + CopySuffix))
                    TryDelete(destination + CopySuffix);
                if (File.Exists(destination))
                {
                    if (SameContent(source, destination))
                    {
                        File.Delete(source);
                        result.Moved++;
                        return;
                    }
                    string parked = Path.Combine(targetRoot, ConflictFolderName, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(parked));
                    if (File.Exists(parked))
                        parked = parked + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    destination = parked;
                    result.Conflicts.Add(relative);
                }
                if (SameVolume(source, destination))
                {
                    File.Move(source, destination);
                }
                else
                {
                    string copy = destination + CopySuffix;
                    File.Copy(source, copy, true);
                    if (!SameContent(source, copy))
                    {
                        TryDelete(copy);
                        throw new IOException("the copy of " + source + " differs");
                    }
                    File.Move(copy, destination);
                    File.Delete(source);
                }
                result.Moved++;
            }
            catch (IOException)
            {
                result.Left.Add(relative);
            }
            catch (UnauthorizedAccessException)
            {
                result.Left.Add(relative);
            }
        }

        // One log line: what was moved where.
        public static string Describe(DataMigrationResult result)
        {
            if (result == null)
                return string.Empty;
            return "Data folder " + result.From + " -> " + result.To + ": " + result.Status + ", " +
                   result.Moved.ToString(CultureInfo.InvariantCulture) + " moved, " +
                   result.Left.Count.ToString(CultureInfo.InvariantCulture) + " still in use, " +
                   result.Conflicts.Count.ToString(CultureInfo.InvariantCulture) + " kept as conflicts" +
                   (result.OldFolderRemoved ? ", old folder removed" : string.Empty);
        }

        private static bool SameVolume(string a, string b)
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameContent(string a, string b)
        {
            FileInfo left = new FileInfo(a);
            FileInfo right = new FileInfo(b);
            if (left.Length != right.Length)
                return false;
            using (SHA256 sha = SHA256.Create())
            using (FileStream x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hx = sha.ComputeHash(x);
                byte[] hy = sha.ComputeHash(y);
                return hx.SequenceEqual(hy);
            }
        }

        private static IEnumerable<string> SafeEntries(Func<string[]> list)
        {
            try
            {
                return list();
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        // Removes the old folder when only empty folders (and the note) are left; never a file that appeared meanwhile.
        private static bool DeleteEmptyTree(string root)
        {
            TryDelete(Path.Combine(root, MovedNoteName));
            foreach (string dir in SafeEntries(() => Directory.GetDirectories(root, "*", SearchOption.AllDirectories)).OrderByDescending(d => d.Length))
                TryDeleteEmpty(dir);
            TryDeleteEmpty(root);
            return !Directory.Exists(root);
        }

        private static void TryDeleteEmpty(string dir)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // True when the link shows JoyPro2 itself: the same entries, and its data-migration.json is the very file of
        // JoyPro2 (same size and time stamp to the tick). A link to anywhere else never matches.
        private static bool PointsTo(string link, string target)
        {
            try
            {
                string linkState = Path.Combine(link, StateFileName);
                string targetState = Path.Combine(target, StateFileName);
                if (!Directory.Exists(target) || !File.Exists(linkState) || !File.Exists(targetState))
                    return false;
                FileInfo a = new FileInfo(linkState);
                FileInfo b = new FileInfo(targetState);
                if (a.Length != b.Length || a.LastWriteTimeUtc.Ticks != b.LastWriteTimeUtc.Ticks)
                    return false;
                HashSet<string> left = new HashSet<string>(Directory.GetFileSystemEntries(link).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                HashSet<string> right = new HashSet<string>(Directory.GetFileSystemEntries(target).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                return left.SetEquals(right);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Removes a junction as a link (RemoveDirectory on the reparse point): the files it shows are not touched.
        private static bool TryRemoveLink(string link)
        {
            try
            {
                if (!IsJunction(link))
                    return false;
                Directory.Delete(link, false);
                return !Directory.Exists(link);
            }
            catch (IOException ex)
            {
                JoyProLog.Warn("Data", "Could not remove the old link " + link, ex);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                JoyProLog.Warn("Data", "Could not remove the old link " + link, ex);
                return false;
            }
        }

        private static void WriteState(string target, string status, DataMigrationResult result)
        {
            try
            {
                StringBuilder json = new StringBuilder();
                json.Append("{\n");
                json.Append("  \"status\": \"").Append(status).Append("\",\n");
                json.Append("  \"from\": \"").Append(Escape(result.From)).Append("\",\n");
                json.Append("  \"to\": \"").Append(Escape(result.To)).Append("\",\n");
                json.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
                json.Append("  \"moved\": ").Append(result.Moved.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                json.Append("  \"oldFolderRemoved\": ").Append(result.OldFolderRemoved ? "true" : "false").Append(",\n");
                json.Append("  \"inUse\": [").Append(string.Join(", ", result.Left.Take(200).Select(l => "\"" + Escape(l) + "\""))).Append("],\n");
                json.Append("  \"conflicts\": [").Append(string.Join(", ", result.Conflicts.Take(200).Select(l => "\"" + Escape(l) + "\""))).Append("]\n");
                json.Append("}\n");
                string file = Path.Combine(target, StateFileName);
                string temp = file + ".tmp";
                File.WriteAllText(temp, json.ToString(), new UTF8Encoding(false));
                if (File.Exists(file))
                    File.Delete(file);
                File.Move(temp, file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder hex = new StringBuilder();
                for (int i = 0; i < 8; i++)
                    hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }

    public sealed class DataMigrationResult
    {
        // "none" (no old folder), "done", "partial" (files in use stayed), "busy" (another process is moving it).
        public string Status = "none";
        public string From = string.Empty;
        public string To = string.Empty;
        public int Moved;
        // The old folder (or the junction 2.4.0 left in its place) was removed by this run.
        public bool OldFolderRemoved;

        // Worth a log line: something was moved or removed, or files are still waiting.
        public bool IsNews
        {
            get { return Moved > 0 || OldFolderRemoved || Status == "partial" || Status == "busy" || Left.Count > 0 || Conflicts.Count > 0; }
        }
        public List<string> Left = new List<string>();
        public List<string> Conflicts = new List<string>();
    }
}
