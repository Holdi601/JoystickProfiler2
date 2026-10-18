#if NET
#nullable disable
#pragma warning disable CA1416
#endif
using System;
using System.IO;

namespace JoyPro.Engine
{
    // File copies and replaces of the exports that survive files carrying the EFS "encrypted" attribute.
    // File.Copy, File.Replace and File.Move keep a file's encryption; where the target cannot hold an encrypted file
    // (a FAT/exFAT drive, a network share, a PC where EFS is off or not available, a Store package folder) Windows
    // stops with ERROR_ENCRYPTION_FAILED (6000, "The specified file could not be encrypted."): an MSFS 2024 export of
    // the Store version failed so when backing up its preset. These helpers copy the bytes instead (the new file has
    // no attributes of the old one), clear the attribute on JoyPro's own temp and backup files, and say plainly what
    // to do when a game file really cannot be written. Plain C# 7.3; the GUI tests link it (SafeFileIoTests).
    internal static class SafeFileIo
    {
        // HRESULT of ERROR_ENCRYPTION_FAILED (Win32 6000).
        public const int EncryptionFailedHResult = unchecked((int)0x80071770);

        // Tests: "copy", "replace", "move" and "stream" (comma list) fail like an EFS target that cannot encrypt.
        public const string FaultVariable = "JOYPRO_TEST_FAULT_ENCRYPTION";

        // Tests in-process: called before each native operation with its name; may throw.
        internal static Action<string> TestFault;

        public static bool IsEncryptionFailure(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e.HResult == EncryptionFailedHResult)
                    return true;
            }
            return false;
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
                catch (IOException ex) when (ownTarget && IsEncryptionFailure(ex))
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

        // temp (JoyPro's own, next to path) takes the place of path, like File.Replace(temp, path, null) or File.Move
        // when path does not exist yet. When Windows cannot carry the encryption over, the bytes of temp are written
        // into path (path keeps its own attributes) and temp is deleted.
        public static void ReplaceFile(string temp, string path)
        {
            ClearEncrypted(temp);
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
                JoyPro.Logging.JoyProLog.Warn("Files", "Replacing '" + path + "' hit ERROR_ENCRYPTION_FAILED; writing its bytes instead: " + ex.Message);
            }
            OverwriteFromTemp(temp, path);
        }

        // temp over path, like File.Copy(temp, path, true) + File.Delete(temp).
        public static void CopyOverAndDelete(string temp, string path)
        {
            ClearEncrypted(temp);
            try
            {
                Fault("copy");
                File.Copy(temp, path, true);
                File.Delete(temp);
                return;
            }
            catch (IOException ex) when (IsEncryptionFailure(ex))
            {
                JoyPro.Logging.JoyProLog.Warn("Files", "Copying over '" + path + "' hit ERROR_ENCRYPTION_FAILED; writing its bytes instead: " + ex.Message);
            }
            OverwriteFromTemp(temp, path);
        }

        // The Encrypted attribute off a file or folder of JoyPro's own (best effort; nothing happens on a plain one).
        public static void ClearEncrypted(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    return;
                if ((File.GetAttributes(path) & FileAttributes.Encrypted) == 0)
                    return;
                File.Decrypt(path);
                JoyPro.Logging.JoyProLog.Info("Files", "Removed the EFS encryption of JoyPro's own '" + path + "'.");
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Files", "Could not remove the encryption of '" + path + "': " + ex.Message);
            }
        }

        // A plain message for a target Windows cannot write because of EFS.
        public static IOException Explain(Exception ex, string target)
        {
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
                if (string.Equals(fault.Trim(), operation, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The specified file could not be encrypted. (test fault: " + operation + ")", EncryptionFailedHResult);
                }
            }
        }
    }

    // A target Windows cannot write because of EFS, with what to do (HResult stays ERROR_ENCRYPTION_FAILED).
    internal sealed class EncryptionTargetException : IOException
    {
        public EncryptionTargetException(string message, Exception inner)
            : base(message, inner)
        {
            HResult = SafeFileIo.EncryptionFailedHResult;
        }
    }
}
