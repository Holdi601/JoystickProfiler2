#if NET
#nullable disable
#pragma warning disable CA1416
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JoyPro.Engine
{
    // File copies and replaces of the exports that survive files carrying the EFS "encrypted" attribute.
    // File.Copy, File.Replace and File.Move keep a file's encryption; where the target cannot hold an encrypted file
    // (a FAT/exFAT drive, a network share, a PC where EFS is off or not available) Windows stops with
    // ERROR_ENCRYPTION_FAILED (6000, "The specified file could not be encrypted."): an MSFS 2024 export failed so when
    // backing up its preset. These helpers copy the bytes instead and say plainly what to do when a game file really
    // cannot be written.
    // The rule: a file JoyPro writes matches the encryption of its folder. In an encrypted folder a new file is
    // encrypted (it takes the folder's mark), and JoyPro never removes the mark there: a Microsoft Store app installed
    // on another drive keeps its data encrypted, and Windows refuses a plain file there with
    // ERROR_APPX_FILE_NOT_ENCRYPTED (409, "The APPX file can not be accessed because it is not encrypted as expected",
    // MSFS 2024 Store report on 2.2.1). Only JoyPro's own folders (backup store, temp) lose the mark, and only when
    // Windows cannot encrypt there. Plain C# 7.3; the GUI tests link it (SafeFileIoTests).
    internal static class SafeFileIo
    {
        // HRESULT of ERROR_ENCRYPTION_FAILED (Win32 6000).
        public const int EncryptionFailedHResult = unchecked((int)0x80071770);

        // HRESULT of ERROR_APPX_FILE_NOT_ENCRYPTED (Win32 409): a plain file where a Store app keeps encrypted data.
        public const int AppxNotEncryptedHResult = unchecked((int)0x80070199);

        // Tests: "copy", "replace", "move" and "stream" (comma list) fail like an EFS target that cannot encrypt.
        public const string FaultVariable = "JOYPRO_TEST_FAULT_ENCRYPTION";

        // Tests in-process: called before each native operation with its name; may throw.
        internal static Action<string> TestFault;

        // Tests in-process: the EFS state of a file or folder (instead of its attribute), File.Encrypt, and each
        // operation on a path ("encrypt", "decrypt", "read", "delete", "move"; may throw).
        internal static Func<string, bool> TestIsEncrypted;
        internal static Action<string> TestEncrypt;
        internal static Action<string, string> TestFaultPath;

        // Either EFS failure: Windows cannot encrypt the target (6000), or a Store app's data must stay encrypted (409).
        public static bool IsEncryptionFailure(Exception ex)
        {
            return HasHResult(ex, EncryptionFailedHResult) || HasHResult(ex, AppxNotEncryptedHResult);
        }

        public static bool IsAppxNotEncrypted(Exception ex)
        {
            return HasHResult(ex, AppxNotEncryptedHResult);
        }

        private static bool HasHResult(Exception ex, int hresult)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e.HResult == hresult)
                    return true;
            }
            return false;
        }

        // The file or folder carries the EFS "encrypted" mark (false when it does not exist or cannot be read).
        public static bool IsEncrypted(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;
                Func<string, bool> probe = TestIsEncrypted;
                if (probe != null)
                    return probe(Path.GetFullPath(path).TrimEnd('\\', '/'));
                if (!File.Exists(path) && !Directory.Exists(path))
                    return false;
                return (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        // The folder that holds path is encrypted: a file written there must be encrypted too.
        public static bool FolderOfIsEncrypted(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            return IsEncrypted(Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/')));
        }

        // A copy of a file's bytes (backups, restores). The target gets no attributes of the source, so an encrypted
        // source never makes the copy fail. ownTarget: the target is JoyPro's own file (backup store, temp), whose
        // folder may lose its encrypted attribute when Windows cannot encrypt there.
        public static void CopyFile(string source, string target, bool overwrite, bool ownTarget)
        {
            if (!overwrite && File.Exists(target))
                throw new IOException("The file '" + target + "' already exists.");
            DateTime written = File.GetLastWriteTimeUtc(source);
            try
            {
                try
                {
                    StreamCopy(source, target);
                }
                catch (IOException ex) when (ownTarget && HasHResult(ex, EncryptionFailedHResult))
                {
                    // JoyPro's own folder is marked encrypted and Windows cannot encrypt there: plain files instead.
                    ClearEncrypted(Path.GetDirectoryName(Path.GetFullPath(target)));
                    ClearEncrypted(target);
                    StreamCopy(source, target);
                }
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                throw Explain(ex, target);
            }
            if (ownTarget)
                ClearEncrypted(target);
            TrySetLastWriteTime(target, written);
        }

        // A copy of a game file next to it ("<file>.jpbak"): File.Copy first, which keeps the source's encryption (in an
        // encrypted folder the copy stays encrypted); when Windows cannot encrypt the target, the bytes (a new file takes
        // its folder's encryption). The game's folder and files keep their mark.
        public static void CopyBesideGameFile(string source, string target)
        {
            DateTime written = File.GetLastWriteTimeUtc(source);
            try
            {
                Fault("copy");
                File.Copy(source, target, true);
                return;
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                JoyPro.Logging.JoyProLog.Warn("Files", "Copying '" + source + "' to '" + target + "' hit an EFS error; copying its bytes instead: " + ex.Message);
            }
            try
            {
                StreamCopy(source, target);
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                throw Explain(ex, target);
            }
            TrySetLastWriteTime(target, written);
        }

        // temp (JoyPro's own, next to path) takes the place of path, like File.Replace(temp, path, null) or File.Move
        // when path does not exist yet. When Windows cannot carry the encryption over, the bytes of temp are written
        // into path (path keeps its own attributes) and temp is deleted. In an encrypted folder temp keeps its mark.
        public static void ReplaceFile(string temp, string path)
        {
            ClearEncryptedTempBeside(temp, path);
            try
            {
                if (File.Exists(path))
                {
                    Fault("replace");
                    File.Replace(temp, path, null);
                }
                else
                {
                    Fault("move");
                    File.Move(temp, path);
                }
                return;
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                JoyPro.Logging.JoyProLog.Warn("Files", "Replacing '" + path + "' hit an EFS error; writing its bytes instead: " + ex.Message);
            }
            OverwriteFromTemp(temp, path);
        }

        // temp over path, like File.Copy(temp, path, true) + File.Delete(temp).
        public static void CopyOverAndDelete(string temp, string path)
        {
            ClearEncryptedTempBeside(temp, path);
            try
            {
                Fault("copy");
                File.Copy(temp, path, true);
                File.Delete(temp);
                return;
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                JoyPro.Logging.JoyProLog.Warn("Files", "Copying over '" + path + "' hit an EFS error; writing its bytes instead: " + ex.Message);
            }
            OverwriteFromTemp(temp, path);
        }

        // JoyPro's temp next to a game file loses an encrypted mark only where the game's folder is plain (a temp that
        // came encrypted into a plain folder); in an encrypted folder it stays encrypted.
        private static void ClearEncryptedTempBeside(string temp, string path)
        {
            if (!FolderOfIsEncrypted(path))
                ClearEncrypted(temp);
        }

        // The Encrypted attribute off a file or folder of JoyPro's own (best effort; nothing happens on a plain one).
        // Never for a game's folder or a file in it.
        public static void ClearEncrypted(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    return;
                if (!IsEncrypted(path))
                    return;
                FaultPath("decrypt", path);
                File.Decrypt(path);
                JoyPro.Logging.JoyProLog.Info("Files", "Removed the EFS encryption of JoyPro's own '" + path + "'.");
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Files", "Could not remove the encryption of '" + path + "': " + ex.Message);
            }
        }

        // Re-encrypts what lost the mark in a folder whose parent is encrypted (JoyPro 2.1.1 to 2.2.1 removed it from
        // the MSFS package they wrote): the folder, its subfolders and files get File.Encrypt. A file Windows does not
        // encrypt is read, deleted and written again (a new file takes the folder's encryption); a file that cannot even
        // be read (409) is moved to quarantineFolder (deleted when that fails) and listed in lost, so the caller can make
        // it again. Nothing happens when the parent is not encrypted. done lists what was changed (for the log).
        public static void MatchFolderEncryption(string folder, string quarantineFolder, List<string> done, List<string> lost)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (!FolderOfIsEncrypted(full))
                return;
            List<string> folders = new List<string> { full };
            folders.AddRange(Directory.GetDirectories(full, "*", SearchOption.AllDirectories).OrderBy(d => d.Length));
            List<string> rebuilt = new List<string>();
            foreach (string dir in folders)
            {
                if (rebuilt.Any(r => dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) || IsEncrypted(dir))
                    continue;
                string error = TryEncrypt(dir);
                if (error == null)
                {
                    done.Add("encrypted folder " + dir);
                    continue;
                }
                // Windows cannot encrypt an existing folder here ("The request is not supported": a tester's PC with
                // the sim installed on another drive), but a folder made in an encrypted folder takes its encryption.
                if (RebuildFolder(dir, error, quarantineFolder, done, lost))
                    rebuilt.Add(dir);
            }
            foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                if (IsEncrypted(file))
                    continue;
                string error = TryEncrypt(file);
                if (error == null)
                {
                    done.Add("encrypted " + file);
                    continue;
                }
                // Written again it would take the mark of a folder that has none: it stays as it is.
                if (!IsEncrypted(Path.GetDirectoryName(file)))
                {
                    done.Add("could not encrypt " + file + ": " + error + " (its folder is not encrypted either)");
                    continue;
                }
                byte[] bytes = null;
                DateTime written = DateTime.MinValue;
                try
                {
                    FaultPath("read", file);
                    written = File.GetLastWriteTimeUtc(file);
                    bytes = File.ReadAllBytes(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
                if (bytes != null)
                {
                    RewriteFile(file, bytes, written, quarantineFolder, done, lost);
                    continue;
                }
                // Not even readable: out of the way, so the caller can make it again.
                RemoveUnreadable(file, error, quarantineFolder, done, lost);
            }
        }

        // Makes a folder that cannot be encrypted again: a new folder beside it (it takes the encryption of the
        // encrypted folder both are in), encrypted files moved into it (they keep their encryption), readable plain
        // files written into it, unreadable ones out of the way (lost), then the old folder goes and the new one takes
        // its name. Subfolders are made the same way inside. False (nothing changed) when the new folder is not
        // encrypted either.
        private static bool RebuildFolder(string dir, string encryptError, string quarantineFolder, List<string> done, List<string> lost)
        {
            string fresh = dir + ".jpnew";
            try
            {
                if (Directory.Exists(fresh))
                    Directory.Delete(fresh, true);
                Directory.CreateDirectory(fresh);
                FaultPath("mkdir", fresh);
                if (!IsEncrypted(fresh))
                {
                    Directory.Delete(fresh, true);
                    done.Add("could not encrypt folder " + dir + ": " + encryptError + " (a new folder there is not encrypted either)");
                    return false;
                }
                FillFolder(dir, fresh, quarantineFolder, done, lost);
                FaultPath("deletedir", dir);
                Directory.Delete(dir, true);
                FaultPath("movedir", fresh);
                Directory.Move(fresh, dir);
                done.Add("made the folder again, encrypted (Windows could not encrypt it: " + encryptError + ") " + dir);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // What was moved goes back, so nothing is missing where the game looks.
                if (Directory.Exists(fresh) && Directory.Exists(dir))
                    MoveBack(fresh, dir);
                done.Add("could not encrypt folder " + dir + ": " + encryptError + "; making it again failed: " + ex.Message);
                return false;
            }
        }

        private static void MoveBack(string from, string to)
        {
            try
            {
                foreach (string file in Directory.GetFiles(from))
                {
                    string target = Path.Combine(to, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Move(file, target);
                }
                foreach (string sub in Directory.GetDirectories(from))
                {
                    string target = Path.Combine(to, Path.GetFileName(sub));
                    Directory.CreateDirectory(target);
                    MoveBack(sub, target);
                }
                Directory.Delete(from, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Info("Files", "Could not move back from '" + from + "': " + ex.Message);
            }
        }

        private static void FillFolder(string from, string to, string quarantineFolder, List<string> done, List<string> lost)
        {
            foreach (string file in Directory.GetFiles(from))
            {
                string target = Path.Combine(to, Path.GetFileName(file));
                if (IsEncrypted(file))
                {
                    try
                    {
                        FaultPath("move", file);
                        File.Move(file, target);
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        JoyPro.Logging.JoyProLog.Debug("Files", "Could not move '" + file + "': " + ex.Message);
                    }
                }
                byte[] bytes = null;
                string error = null;
                DateTime written = DateTime.MinValue;
                try
                {
                    FaultPath("read", file);
                    written = File.GetLastWriteTimeUtc(file);
                    bytes = File.ReadAllBytes(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
                if (bytes == null)
                {
                    RemoveUnreadable(file, error, quarantineFolder, done, lost);
                    continue;
                }
                FaultPath("write", target);
                File.WriteAllBytes(target, bytes);
                TrySetLastWriteTime(target, written);
                DeleteFile(file);
                done.Add("wrote again (takes the folder's encryption) " + Path.Combine(from, Path.GetFileName(file)));
            }
            foreach (string sub in Directory.GetDirectories(from))
            {
                string target = Path.Combine(to, Path.GetFileName(sub));
                Directory.CreateDirectory(target);
                FaultPath("mkdir", target);
                FillFolder(sub, target, quarantineFolder, done, lost);
            }
        }

        private static void RewriteFile(string file, byte[] bytes, DateTime written, string quarantineFolder, List<string> done, List<string> lost)
        {
            try
            {
                DeleteFile(file);
                File.WriteAllBytes(file, bytes);
                TrySetLastWriteTime(file, written);
                done.Add("wrote again (takes the folder's encryption) " + file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (File.Exists(file))
                {
                    done.Add("could not write again " + file + ": " + ex.Message);
                    return;
                }
                // Deleted but not written: the bytes go where JoyPro keeps its copies.
                try
                {
                    if (!string.IsNullOrWhiteSpace(quarantineFolder))
                    {
                        Directory.CreateDirectory(quarantineFolder);
                        File.WriteAllBytes(Path.Combine(quarantineFolder, Path.GetFileName(file)), bytes);
                    }
                }
                catch (Exception saveEx) when (saveEx is IOException || saveEx is UnauthorizedAccessException)
                {
                }
                lost.Add(file);
                done.Add("removed (could not be written again: " + ex.Message + ") " + file);
            }
        }

        private static void RemoveUnreadable(string file, string error, string quarantineFolder, List<string> done, List<string> lost)
        {
            bool moved = false;
            if (!string.IsNullOrWhiteSpace(quarantineFolder))
            {
                try
                {
                    Directory.CreateDirectory(quarantineFolder);
                    string target = Path.Combine(quarantineFolder, Path.GetFileName(file));
                    if (File.Exists(target))
                        File.Delete(target);
                    FaultPath("move", file);
                    File.Move(file, target);
                    moved = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            try
            {
                if (!moved)
                    DeleteFile(file);
                lost.Add(file);
                done.Add((moved ? "moved the unreadable file to " + quarantineFolder + ": " : "deleted the unreadable file ") + file + " (" + error + ")");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                done.Add("could not remove the unreadable file " + file + ": " + ex.Message);
            }
        }

        // File.Encrypt; null when it worked, else the reason.
        private static string TryEncrypt(string path)
        {
            try
            {
                FaultPath("encrypt", path);
                Action<string> hook = TestEncrypt;
                if (hook != null)
                    hook(Path.GetFullPath(path).TrimEnd('\\', '/'));
                else
                    File.Encrypt(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is PlatformNotSupportedException)
            {
                return ex.Message;
            }
        }

        private static void DeleteFile(string path)
        {
            FaultPath("delete", path);
            File.Delete(path);
        }

        // The bytes of a file (shared read). Tests: JOYPRO_TEST_FAULT_ENCRYPTION "read:409:<file name>" makes the read
        // of that file fail like a plain file in a Store app's encrypted data.
        public static byte[] ReadAllBytes(string path)
        {
            FaultPath("read", path);
            string faults = Environment.GetEnvironmentVariable(FaultVariable);
            if (!string.IsNullOrWhiteSpace(faults))
            {
                string name = Path.GetFileName(path);
                foreach (string fault in faults.Split(','))
                {
                    string entry = fault.Trim();
                    if (entry.StartsWith("read:409:", StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Substring(9), name, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The APPX file can not be accessed because it is not encrypted as expected. (test fault: read)", AppxNotEncryptedHResult);
                }
            }
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        // File.Encrypt (EFS) of one file or folder; null when it worked, else the reason.
        public static string TryEncryptPath(string path)
        {
            return TryEncrypt(path);
        }

        // Why a file in a Store app's encrypted data cannot be read (409).
        public static string ExplainRead(Exception ex, string target)
        {
            return "Windows could not read '" + target + "': the game keeps its data encrypted (EFS) on this PC (a Microsoft Store app), and this file " +
                   "is not encrypted as expected, so neither the game nor JoyPro can read it (" + (ex == null ? "409" : ex.Message.Trim()) + ").";
        }

        // A plain message for a target Windows cannot write because of EFS.
        public static IOException Explain(Exception ex, string target)
        {
            if (IsAppxNotEncrypted(ex))
            {
                return new EncryptionTargetException(
                    "Windows could not access '" + target + "': the game keeps its data encrypted on this drive (a Microsoft Store app installed on " +
                    "another drive), and this file is not encrypted as expected, so JoyPro could not write it. Export again (JoyPro repairs the " +
                    "encryption of its own files); if it still fails, delete that file (JoyPro makes it again) and export again.",
                    ex, AppxNotEncryptedHResult);
            }
            return new EncryptionTargetException(
                "Windows could not write '" + target + "': the file or its folder is marked as encrypted (EFS), and encrypted files " +
                "cannot be stored there (a FAT/exFAT drive, a network share, or EFS is not available on this PC). Remove the encryption " +
                "(Properties > Advanced > untick 'Encrypt contents') of that file and folder, or move the game's folder to an NTFS drive, then export again.",
                ex);
        }

        private static void OverwriteFromTemp(string temp, string path)
        {
            try
            {
                StreamCopy(temp, path);
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                throw Explain(ex, path);
            }
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void StreamCopy(string source, string target)
        {
            Fault("stream");
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            // OpenOrCreate + SetLength: an existing hidden or system file is overwritten too (Create refuses those).
            using (FileStream output = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output, 81920);
                output.SetLength(output.Position);
            }
        }

        private static void TrySetLastWriteTime(string path, DateTime utc)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, utc);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void FaultPath(string operation, string path)
        {
            Action<string, string> hook = TestFaultPath;
            if (hook != null)
                hook(operation, Path.GetFullPath(path).TrimEnd('\\', '/'));
        }

        private static void Fault(string operation)
        {
            Action<string> hook = TestFault;
            if (hook != null)
                hook(operation);
            string faults = Environment.GetEnvironmentVariable(FaultVariable);
            if (string.IsNullOrWhiteSpace(faults))
                return;
            foreach (string fault in faults.Split(','))
            {
                string name = fault.Trim();
                if (string.Equals(name, operation, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The specified file could not be encrypted. (test fault: " + operation + ")", EncryptionFailedHResult);
                }
                // "<operation>:409": the same operation fails like a plain file in a Store app's encrypted data.
                if (string.Equals(name, operation + ":409", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The APPX file can not be accessed because it is not encrypted as expected. (test fault: " + operation + ")", AppxNotEncryptedHResult);
                }
            }
        }
    }

    // A target Windows cannot write because of EFS, with what to do (HResult stays the EFS error's).
    internal sealed class EncryptionTargetException : IOException
    {
        public EncryptionTargetException(string message, Exception inner)
            : this(message, inner, SafeFileIo.EncryptionFailedHResult)
        {
        }

        public EncryptionTargetException(string message, Exception inner, int hresult)
            : base(message, inner)
        {
            HResult = hresult;
        }
    }
}
