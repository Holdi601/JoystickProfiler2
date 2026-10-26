using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Text;

namespace JoyPro
{
    // The engine's one chokepoint for file system writes (JoyPro.Engine.TestSandbox, docs/testing.md "Test sandbox"). These types have
    // the names of System.IO's and live in the root namespace JoyPro, so C#'s name lookup takes them for every "File.", "Directory."
    // and "new FileStream(" of the engine (JoyPro and every sub namespace: Engine, Games, CockpitPictures, Logging, ...) (a namespace's own types win over its using
    // directives). Reads pass straight through; every write asks JoyPro.Engine.TestSandbox.EnsureWritable first, which does nothing
    // unless JOYPRO_TEST_SANDBOX is set. Plain C# 7.3, engine only.
    internal static class File
    {
        // ---- reads ----
        public static bool Exists(string path) { return System.IO.File.Exists(path); }
        public static string ReadAllText(string path) { return System.IO.File.ReadAllText(path); }
        public static string ReadAllText(string path, Encoding encoding) { return System.IO.File.ReadAllText(path, encoding); }
        public static byte[] ReadAllBytes(string path) { return System.IO.File.ReadAllBytes(path); }
        public static string[] ReadAllLines(string path) { return System.IO.File.ReadAllLines(path); }
        public static string[] ReadAllLines(string path, Encoding encoding) { return System.IO.File.ReadAllLines(path, encoding); }
        public static IEnumerable<string> ReadLines(string path) { return System.IO.File.ReadLines(path); }
        public static IEnumerable<string> ReadLines(string path, Encoding encoding) { return System.IO.File.ReadLines(path, encoding); }
        public static DateTime GetLastWriteTimeUtc(string path) { return System.IO.File.GetLastWriteTimeUtc(path); }
        public static DateTime GetLastWriteTime(string path) { return System.IO.File.GetLastWriteTime(path); }
        public static DateTime GetCreationTimeUtc(string path) { return System.IO.File.GetCreationTimeUtc(path); }
        public static DateTime GetCreationTime(string path) { return System.IO.File.GetCreationTime(path); }
        public static DateTime GetLastAccessTimeUtc(string path) { return System.IO.File.GetLastAccessTimeUtc(path); }
        public static FileAttributes GetAttributes(string path) { return System.IO.File.GetAttributes(path); }
        public static FileStream OpenRead(string path) { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        public static StreamReader OpenText(string path) { return System.IO.File.OpenText(path); }

        // ---- writes ----
        public static void WriteAllText(string path, string contents) { System.IO.File.WriteAllText(W(path, "write"), contents); }
        public static void WriteAllText(string path, string contents, Encoding encoding) { System.IO.File.WriteAllText(W(path, "write"), contents, encoding); }
        public static void WriteAllBytes(string path, byte[] bytes) { System.IO.File.WriteAllBytes(W(path, "write"), bytes); }
        public static void WriteAllLines(string path, string[] contents) { System.IO.File.WriteAllLines(W(path, "write"), contents); }
        public static void WriteAllLines(string path, string[] contents, Encoding encoding) { System.IO.File.WriteAllLines(W(path, "write"), contents, encoding); }
        public static void WriteAllLines(string path, IEnumerable<string> contents) { System.IO.File.WriteAllLines(W(path, "write"), contents); }
        public static void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding) { System.IO.File.WriteAllLines(W(path, "write"), contents, encoding); }
        public static void AppendAllText(string path, string contents) { System.IO.File.AppendAllText(W(path, "append"), contents); }
        public static void AppendAllText(string path, string contents, Encoding encoding) { System.IO.File.AppendAllText(W(path, "append"), contents, encoding); }
        public static void AppendAllLines(string path, IEnumerable<string> contents) { System.IO.File.AppendAllLines(W(path, "append"), contents); }
        public static void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding) { System.IO.File.AppendAllLines(W(path, "append"), contents, encoding); }
        public static StreamWriter CreateText(string path) { return new StreamWriter(path, false); }
        public static StreamWriter AppendText(string path) { return new StreamWriter(path, true); }
        public static FileStream Create(string path) { return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None); }
        public static FileStream Create(string path, int bufferSize) { return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize); }
        public static FileStream Create(string path, int bufferSize, FileOptions options) { return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize, options); }
        public static FileStream OpenWrite(string path) { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        public static FileStream Open(string path, FileMode mode) { return new FileStream(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None); }
        public static FileStream Open(string path, FileMode mode, FileAccess access) { return new FileStream(path, mode, access, FileShare.None); }
        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) { return new FileStream(path, mode, access, share); }
        public static void Delete(string path) { System.IO.File.Delete(W(path, "delete")); }
        public static void Copy(string sourceFileName, string destFileName) { System.IO.File.Copy(sourceFileName, W(destFileName, "copy to")); }
        public static void Copy(string sourceFileName, string destFileName, bool overwrite) { System.IO.File.Copy(sourceFileName, W(destFileName, "copy to"), overwrite); }
        public static void Move(string sourceFileName, string destFileName) { System.IO.File.Move(W(sourceFileName, "move from"), W(destFileName, "move to")); }
        public static void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
        {
            System.IO.File.Replace(W(sourceFileName, "replace from"), W(destinationFileName, "replace"), W(destinationBackupFileName, "replace backup"));
        }
        public static void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName, bool ignoreMetadataErrors)
        {
            System.IO.File.Replace(W(sourceFileName, "replace from"), W(destinationFileName, "replace"), W(destinationBackupFileName, "replace backup"), ignoreMetadataErrors);
        }
        public static void SetAttributes(string path, FileAttributes fileAttributes) { System.IO.File.SetAttributes(W(path, "set attributes of"), fileAttributes); }
        public static void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) { System.IO.File.SetLastWriteTimeUtc(W(path, "set time of"), lastWriteTimeUtc); }
        public static void SetLastWriteTime(string path, DateTime lastWriteTime) { System.IO.File.SetLastWriteTime(W(path, "set time of"), lastWriteTime); }
        public static void SetCreationTimeUtc(string path, DateTime creationTimeUtc) { System.IO.File.SetCreationTimeUtc(W(path, "set time of"), creationTimeUtc); }
        public static void Encrypt(string path) { System.IO.File.Encrypt(W(path, "encrypt")); }
        public static void Decrypt(string path) { System.IO.File.Decrypt(W(path, "decrypt")); }
        public static void SetAccessControl(string path, FileSecurity fileSecurity) { System.IO.File.SetAccessControl(W(path, "set access of"), fileSecurity); }

        private static string W(string path, string operation)
        {
            return JoyPro.Engine.TestSandbox.EnsureWritable(path, "File " + operation);
        }
    }

    internal static class Directory
    {
        // ---- reads ----
        public static bool Exists(string path) { return System.IO.Directory.Exists(path); }
        public static string[] GetFiles(string path) { return System.IO.Directory.GetFiles(path); }
        public static string[] GetFiles(string path, string searchPattern) { return System.IO.Directory.GetFiles(path, searchPattern); }
        public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.GetFiles(path, searchPattern, searchOption); }
        public static string[] GetDirectories(string path) { return System.IO.Directory.GetDirectories(path); }
        public static string[] GetDirectories(string path, string searchPattern) { return System.IO.Directory.GetDirectories(path, searchPattern); }
        public static string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.GetDirectories(path, searchPattern, searchOption); }
        public static string[] GetFileSystemEntries(string path) { return System.IO.Directory.GetFileSystemEntries(path); }
        public static string[] GetFileSystemEntries(string path, string searchPattern) { return System.IO.Directory.GetFileSystemEntries(path, searchPattern); }
        public static string[] GetFileSystemEntries(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.GetFileSystemEntries(path, searchPattern, searchOption); }
        public static IEnumerable<string> EnumerateFiles(string path) { return System.IO.Directory.EnumerateFiles(path); }
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern) { return System.IO.Directory.EnumerateFiles(path, searchPattern); }
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.EnumerateFiles(path, searchPattern, searchOption); }
        public static IEnumerable<string> EnumerateDirectories(string path) { return System.IO.Directory.EnumerateDirectories(path); }
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern) { return System.IO.Directory.EnumerateDirectories(path, searchPattern); }
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.EnumerateDirectories(path, searchPattern, searchOption); }
        public static IEnumerable<string> EnumerateFileSystemEntries(string path) { return System.IO.Directory.EnumerateFileSystemEntries(path); }
        public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern) { return System.IO.Directory.EnumerateFileSystemEntries(path, searchPattern); }
        public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption) { return System.IO.Directory.EnumerateFileSystemEntries(path, searchPattern, searchOption); }
        public static DirectoryInfo GetParent(string path) { return System.IO.Directory.GetParent(path); }
        public static DateTime GetLastWriteTime(string path) { return System.IO.Directory.GetLastWriteTime(path); }
        public static DateTime GetLastWriteTimeUtc(string path) { return System.IO.Directory.GetLastWriteTimeUtc(path); }
        public static DateTime GetCreationTimeUtc(string path) { return System.IO.Directory.GetCreationTimeUtc(path); }
        public static string GetCurrentDirectory() { return System.IO.Directory.GetCurrentDirectory(); }
        public static string GetDirectoryRoot(string path) { return System.IO.Directory.GetDirectoryRoot(path); }
        public static string[] GetLogicalDrives() { return System.IO.Directory.GetLogicalDrives(); }

        // ---- writes ----
        // An existing folder is not written by "create": only a folder that does not exist yet is checked.
        public static DirectoryInfo CreateDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !System.IO.Directory.Exists(path))
                JoyPro.Engine.TestSandbox.EnsureWritable(path, "Directory create");
            return System.IO.Directory.CreateDirectory(path);
        }
        public static void Delete(string path) { System.IO.Directory.Delete(W(path, "delete"), false); }
        public static void Delete(string path, bool recursive) { System.IO.Directory.Delete(W(path, "delete"), recursive); }
        public static void Move(string sourceDirName, string destDirName) { System.IO.Directory.Move(W(sourceDirName, "move from"), W(destDirName, "move to")); }
        public static void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) { System.IO.Directory.SetLastWriteTimeUtc(W(path, "set time of"), lastWriteTimeUtc); }
        public static void SetCurrentDirectory(string path) { System.IO.Directory.SetCurrentDirectory(path); }

        private static string W(string path, string operation)
        {
            return JoyPro.Engine.TestSandbox.EnsureWritable(path, "Directory " + operation);
        }
    }

    // "new StreamWriter(path, ...)" of the engine: the file is checked (a writer on a stream was checked with its stream).
    internal class StreamWriter : System.IO.StreamWriter
    {
        public StreamWriter(Stream stream) : base(stream) { }
        public StreamWriter(Stream stream, Encoding encoding) : base(stream, encoding) { }
        public StreamWriter(Stream stream, Encoding encoding, int bufferSize) : base(stream, encoding, bufferSize) { }
        public StreamWriter(Stream stream, Encoding encoding, int bufferSize, bool leaveOpen) : base(stream, encoding, bufferSize, leaveOpen) { }
        public StreamWriter(string path) : base(W(path)) { }
        public StreamWriter(string path, bool append) : base(W(path), append) { }
        public StreamWriter(string path, bool append, Encoding encoding) : base(W(path), append, encoding) { }
        public StreamWriter(string path, bool append, Encoding encoding, int bufferSize) : base(W(path), append, encoding, bufferSize) { }

        private static string W(string path)
        {
            return JoyPro.Engine.TestSandbox.EnsureWritable(path, "StreamWriter");
        }
    }

    // "new FileStream(path, ...)" of the engine: a stream that can write (any mode but Open, or write access) is checked.
    internal class FileStream : System.IO.FileStream
    {
        public FileStream(string path, FileMode mode)
            : base(Check(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite), mode)
        {
        }

        public FileStream(string path, FileMode mode, FileAccess access)
            : base(Check(path, mode, access), mode, access)
        {
        }

        public FileStream(string path, FileMode mode, FileAccess access, FileShare share)
            : base(Check(path, mode, access), mode, access, share)
        {
        }

        public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
            : base(Check(path, mode, access), mode, access, share, bufferSize)
        {
        }

        public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync)
            : base(Check(path, mode, access), mode, access, share, bufferSize, useAsync)
        {
        }

        public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
            : base(Check(path, mode, access), mode, access, share, bufferSize, options)
        {
        }

        private static string Check(string path, FileMode mode, FileAccess access)
        {
            if (mode != FileMode.Open || (access & FileAccess.Write) != 0)
                JoyPro.Engine.TestSandbox.EnsureWritable(path, "FileStream " + mode + "/" + access);
            return path;
        }
    }
}
