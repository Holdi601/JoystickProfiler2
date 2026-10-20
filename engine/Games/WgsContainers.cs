using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // The Xbox connected storage ("wgs") of a Microsoft Store game, read and written byte for byte (docs/games/msfs.md
    // "Writing the Microsoft Store profiles"). Layout as the open-source save tools write it (libNOM.io, the No Man's
    // Sky save library: PlatformMicrosoft_Write.cs; GamePassStorage docs/wgs-format.md) and as found in a tester's MSFS
    // 2024 save (21 containers, version 14, every entry state 1, header flags 3):
    //   containers.index: uint32 version (14), uint32 container count, uint32 0, string package ("<family>!App"),
    //     FILETIME (the store's recency token), uint32 sync flags (1 fully uploaded, 2 fully downloaded, 16 unresolved
    //     conflict), string root id, 8 bytes (00 00 00 10 00 00 00 00); per container: string name, string name again,
    //     string ETag ("0x8D..." of the cloud, "" when never uploaded), byte number N of container.N, uint32 state
    //     (1 synced, 2 modified, 3 deleted, 5 created), 16-byte folder GUID, FILETIME (ms), 8 bytes 0, uint64 size of
    //     the blobs. Strings: int32 length in characters, UTF-16LE.
    //   <folder>\container.N: uint32 4, uint32 blob count; per blob 128 bytes of UTF-16 name ("data", NUL padded),
    //     16-byte id the cloud knows, 16-byte id of the file on disk (equal when settled).
    //   <folder>\<on-disk id, 32 hex digits>: the blob.
    internal sealed class WgsIndexEntry
    {
        public string Name;
        public string Name2;
        public string ETag;
        public byte Number;
        public uint State;
        public Guid Folder;
        public long FileTime;
        public byte[] Reserved = new byte[8];
        public long Size;

        public const uint StateSynced = 1;
        public const uint StateModified = 2;
        public const uint StateDeleted = 3;
        public const uint StateCreated = 5;

        public WgsIndexEntry Clone()
        {
            return new WgsIndexEntry { Name = Name, Name2 = Name2, ETag = ETag, Number = Number, State = State, Folder = Folder, FileTime = FileTime, Reserved = (byte[])Reserved.Clone(), Size = Size };
        }

        public string FolderName
        {
            get { return Folder.ToString("N").ToUpperInvariant(); }
        }
    }

    internal sealed class WgsIndex
    {
        public const uint KnownVersion = 14;
        public const uint FlagFullyUploaded = 1;
        public const uint FlagUnresolvedConflict = 16;

        public uint Version;
        public uint Reserved;
        public string PackageName;
        public long FileTime;
        public uint SyncFlags;
        public string RootId;
        public byte[] Trailing = new byte[8];
        public List<WgsIndexEntry> Entries = new List<WgsIndexEntry>();

        public WgsIndexEntry Find(string name)
        {
            return Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Strict: every byte must be accounted for, else InvalidDataException (another layout: nothing is written).
        public static WgsIndex Parse(byte[] data)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(data, false)))
            {
                try
                {
                    WgsIndex index = new WgsIndex();
                    index.Version = reader.ReadUInt32();
                    uint count = reader.ReadUInt32();
                    if (count > 100000)
                        throw new InvalidDataException("container count " + count.ToString(CultureInfo.InvariantCulture));
                    index.Reserved = reader.ReadUInt32();
                    index.PackageName = ReadString(reader);
                    index.FileTime = reader.ReadInt64();
                    index.SyncFlags = reader.ReadUInt32();
                    index.RootId = ReadString(reader);
                    index.Trailing = reader.ReadBytes(8);
                    for (uint i = 0; i < count; i++)
                    {
                        WgsIndexEntry entry = new WgsIndexEntry();
                        entry.Name = ReadString(reader);
                        entry.Name2 = ReadString(reader);
                        entry.ETag = ReadString(reader);
                        entry.Number = reader.ReadByte();
                        entry.State = reader.ReadUInt32();
                        entry.Folder = new Guid(Exactly(reader, 16));
                        entry.FileTime = reader.ReadInt64();
                        entry.Reserved = Exactly(reader, 8);
                        entry.Size = reader.ReadInt64();
                        index.Entries.Add(entry);
                    }
                    if (index.Trailing.Length != 8 || reader.BaseStream.Position != data.Length)
                        throw new InvalidDataException((data.Length - reader.BaseStream.Position).ToString(CultureInfo.InvariantCulture) + " unknown byte(s) after the containers");
                    return index;
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("the index ends early");
                }
            }
        }

        public byte[] ToBytes()
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write(Version);
                writer.Write((uint)Entries.Count);
                writer.Write(Reserved);
                WriteString(writer, PackageName);
                writer.Write(FileTime);
                writer.Write(SyncFlags);
                WriteString(writer, RootId);
                writer.Write(Trailing);
                foreach (WgsIndexEntry entry in Entries)
                {
                    WriteString(writer, entry.Name);
                    WriteString(writer, entry.Name2);
                    WriteString(writer, entry.ETag);
                    writer.Write(entry.Number);
                    writer.Write(entry.State);
                    writer.Write(entry.Folder.ToByteArray());
                    writer.Write(entry.FileTime);
                    writer.Write(entry.Reserved);
                    writer.Write(entry.Size);
                }
                writer.Flush();
                return memory.ToArray();
            }
        }

        internal static byte[] Exactly(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
                throw new EndOfStreamException();
            return bytes;
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 4096)
                throw new InvalidDataException("string length " + length.ToString(CultureInfo.InvariantCulture));
            return Encoding.Unicode.GetString(Exactly(reader, length * 2));
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            value = value ?? string.Empty;
            writer.Write(value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }
    }

    internal sealed class WgsContainerBlob
    {
        public byte[] NameField = new byte[128];
        public Guid CloudId;
        public Guid DiskId;

        public string Name
        {
            get
            {
                string name = Encoding.Unicode.GetString(NameField);
                int nul = name.IndexOf('\0');
                return nul >= 0 ? name.Substring(0, nul) : name;
            }
        }

        public string FileName
        {
            get { return DiskId.ToString("N").ToUpperInvariant(); }
        }
    }

    internal sealed class WgsContainerFile
    {
        public uint Header;
        public List<WgsContainerBlob> Blobs = new List<WgsContainerBlob>();
        public byte[] Trailing = new byte[0];

        public static WgsContainerFile Parse(byte[] data)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(data, false)))
            {
                try
                {
                    WgsContainerFile file = new WgsContainerFile();
                    file.Header = reader.ReadUInt32();
                    uint count = reader.ReadUInt32();
                    if (count == 0 || count > 1024 || 8 + count * 160L > data.Length)
                        throw new InvalidDataException("blob count " + count.ToString(CultureInfo.InvariantCulture));
                    for (uint i = 0; i < count; i++)
                    {
                        WgsContainerBlob blob = new WgsContainerBlob();
                        blob.NameField = WgsIndex.Exactly(reader, 128);
                        blob.CloudId = new Guid(WgsIndex.Exactly(reader, 16));
                        blob.DiskId = new Guid(WgsIndex.Exactly(reader, 16));
                        file.Blobs.Add(blob);
                    }
                    file.Trailing = reader.ReadBytes(data.Length - (int)reader.BaseStream.Position);
                    return file;
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("the container file ends early");
                }
            }
        }

        public byte[] ToBytes()
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write(Header);
                writer.Write((uint)Blobs.Count);
                foreach (WgsContainerBlob blob in Blobs)
                {
                    writer.Write(blob.NameField);
                    writer.Write(blob.CloudId.ToByteArray());
                    writer.Write(blob.DiskId.ToByteArray());
                }
                writer.Write(Trailing);
                writer.Flush();
                return memory.ToArray();
            }
        }

        public static string FileName(byte number)
        {
            return "container." + number.ToString(CultureInfo.InvariantCulture);
        }
    }

    // One container of a user folder as it stands: its entry, container file and the single blob (MSFS keeps one
    // file per container: inputprofile_<n>, profile_00, the logbooks).
    internal sealed class WgsContainerState
    {
        public WgsIndexEntry Entry;
        public string FolderPath;
        public string ContainerPath;
        public WgsContainerFile Container;
        public string BlobPath;
    }

    // Writes new contents of containers of one user folder (<wgs>\<user>_<title>\) the way the save tools and the game
    // do: per container a new blob (new GUID name) and container.<N+1> naming it (the cloud's id kept, the new file as
    // on-disk id), then containers.index once (entry: N+1, size, time, state synced -> modified; header: newer time,
    // "fully uploaded" cleared), read back and checked; only then the superseded files go. Anything unexpected throws
    // WgsUnexpectedException before the first byte is written.
    internal static class WgsWriter
    {
        // Tests: a fault after the given step ("blob", "container", "index") to check the rollback.
        public const string TestFaultVariable = "JOYPRO_TEST_FAULT_WGS";

        public static string IndexPath(string userFolder)
        {
            return Path.Combine(userFolder, "containers.index");
        }

        public static bool IsStoreIndex(string path)
        {
            return !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(path), "containers.index", StringComparison.OrdinalIgnoreCase) &&
                   path.Replace('/', '\\').IndexOf("\\SystemAppData\\wgs\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // After a backup of the save was put back (engine backups restore): the containers that differ from the state
        // before the restore count as changed on this PC (state modified, the newest cloud version tag known kept), the
        // index time grows and "fully uploaded" is cleared, so the Xbox sync uploads the restored profiles instead of
        // downloading its newer copy. Returns what was done ("" when nothing).
        public static string MarkRestored(string indexPath, byte[] before, DateTime utcNow)
        {
            try
            {
                WgsIndex restored = WgsIndex.Parse(File.ReadAllBytes(indexPath));
                WgsIndex current = null;
                if (before != null)
                {
                    try
                    {
                        current = WgsIndex.Parse(before);
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
                int marked = 0;
                foreach (WgsIndexEntry entry in restored.Entries)
                {
                    WgsIndexEntry now = current == null ? null : current.Find(entry.Name);
                    if (now != null && now.Number == entry.Number && now.Size == entry.Size && now.Folder == entry.Folder)
                        continue;
                    if (now != null && !string.IsNullOrEmpty(now.ETag))
                    {
                        // The cloud has (at least) that version: the restored content goes up as a change of it.
                        entry.ETag = now.ETag;
                        if (entry.State == WgsIndexEntry.StateCreated)
                            entry.State = WgsIndexEntry.StateModified;
                    }
                    if (entry.State == WgsIndexEntry.StateSynced)
                        entry.State = WgsIndexEntry.StateModified;
                    entry.FileTime = utcNow.ToFileTimeUtc() / 10000 * 10000;
                    marked++;
                }
                long newest = Math.Max(restored.FileTime, current == null ? 0 : current.FileTime);
                restored.FileTime = Math.Max(utcNow.ToFileTimeUtc(), newest + 1);
                restored.SyncFlags &= ~WgsIndex.FlagFullyUploaded;
                byte[] bytes = restored.ToBytes();
                string temp = indexPath + ".jptmp";
                File.WriteAllBytes(temp, bytes);
                File.Replace(temp, indexPath, null);
                JoyPro.Logging.JoyProLog.Info("Backups", "Microsoft Store save index after restore: " + marked.ToString(CultureInfo.InvariantCulture) + " container(s) marked as changed on this PC, index time raised: " + indexPath);
                return marked.ToString(CultureInfo.InvariantCulture) + " restored save container(s) marked for upload to the Xbox cloud";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                JoyPro.Logging.JoyProLog.Warn("Backups", "Could not mark the restored Microsoft Store save " + indexPath + " as changed", ex);
                return string.Empty;
            }
        }

        // The user folders of a wgs folder (each holds a containers.index): <wgs>\<user>_<title>.
        public static List<string> UserFolders(string wgsFolder)
        {
            List<string> folders = new List<string>();
            if (string.IsNullOrWhiteSpace(wgsFolder) || !Directory.Exists(wgsFolder))
                return folders;
            if (File.Exists(IndexPath(wgsFolder)))
            {
                folders.Add(wgsFolder);
                return folders;
            }
            foreach (string dir in Directory.GetDirectories(wgsFolder))
            {
                if (File.Exists(IndexPath(dir)))
                    folders.Add(dir);
            }
            return folders;
        }

        // The index and the named containers, checked: version 14, no unresolved conflict, each named container listed
        // once with a usable state, its container.N there with exactly one blob, the blob there with the size the index
        // gives. Throws WgsUnexpectedException with the reason otherwise.
        public static WgsIndex ReadChecked(string userFolder, IEnumerable<string> names, out Dictionary<string, WgsContainerState> states)
        {
            states = new Dictionary<string, WgsContainerState>(StringComparer.OrdinalIgnoreCase);
            string indexPath = IndexPath(userFolder);
            WgsIndex index;
            try
            {
                index = WgsIndex.Parse(File.ReadAllBytes(indexPath));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                throw new WgsUnexpectedException("the save index " + indexPath + " could not be read (" + ex.Message + ")");
            }
            if (index.Version != WgsIndex.KnownVersion)
                throw new WgsUnexpectedException("the save index has version " + index.Version.ToString(CultureInfo.InvariantCulture) + " (JoyPro knows version " + WgsIndex.KnownVersion.ToString(CultureInfo.InvariantCulture) + ")");
            if ((index.SyncFlags & WgsIndex.FlagUnresolvedConflict) != 0)
                throw new WgsUnexpectedException("the Xbox cloud sync has an unresolved conflict in this save (start the sim once and pick a version)");
            List<string> duplicates = index.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                throw new WgsUnexpectedException("the save index lists " + string.Join(", ", duplicates) + " more than once");
            foreach (string name in names)
            {
                WgsIndexEntry entry = index.Find(name);
                if (entry == null)
                    throw new WgsUnexpectedException("the save index does not list " + name);
                if (entry.State != WgsIndexEntry.StateSynced && entry.State != WgsIndexEntry.StateModified && entry.State != WgsIndexEntry.StateCreated)
                    throw new WgsUnexpectedException(name + " has the sync state " + entry.State.ToString(CultureInfo.InvariantCulture) + " (deleted or unknown)");
                if ((entry.State == WgsIndexEntry.StateCreated) != string.IsNullOrEmpty(entry.ETag))
                    throw new WgsUnexpectedException(name + ": sync state " + entry.State.ToString(CultureInfo.InvariantCulture) + " does not fit its cloud version tag");
                WgsContainerState state = new WgsContainerState { Entry = entry, FolderPath = Path.Combine(userFolder, entry.FolderName) };
                state.ContainerPath = Path.Combine(state.FolderPath, WgsContainerFile.FileName(entry.Number));
                try
                {
                    state.Container = WgsContainerFile.Parse(File.ReadAllBytes(state.ContainerPath));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    throw new WgsUnexpectedException("the container file of " + name + " (" + state.ContainerPath + ") could not be read (" + ex.Message + ")");
                }
                if (state.Container.Blobs.Count != 1)
                    throw new WgsUnexpectedException(name + " has " + state.Container.Blobs.Count.ToString(CultureInfo.InvariantCulture) + " blobs (JoyPro expects 1)");
                // Differing ids: a local change the cloud has not taken yet (JoyPro's own last write does that too);
                // only a container the index calls synced must have equal ones.
                if (state.Container.Blobs[0].CloudId != state.Container.Blobs[0].DiskId && entry.State == WgsIndexEntry.StateSynced)
                    throw new WgsUnexpectedException(name + " is being synced with the Xbox cloud (its blob ids differ); start and close the sim once, then export again");
                state.BlobPath = Path.Combine(state.FolderPath, state.Container.Blobs[0].FileName);
                FileInfo blob = new FileInfo(state.BlobPath);
                if (!blob.Exists)
                    throw new WgsUnexpectedException("the blob of " + name + " is missing (" + state.BlobPath + ")");
                if (blob.Length != entry.Size)
                    throw new WgsUnexpectedException("the blob of " + name + " has " + blob.Length.ToString(CultureInfo.InvariantCulture) + " bytes, the index says " + entry.Size.ToString(CultureInfo.InvariantCulture));
                states[name] = state;
            }
            return index;
        }

        public static byte[] ReadBlob(WgsContainerState state)
        {
            return File.ReadAllBytes(state.BlobPath);
        }

        // Writes the new contents. rollback is called when anything fails after the first write (it puts the backup
        // back); the superseded files are removed only after the read-back check. Returns the new blob paths.
        public static Dictionary<string, string> Write(string userFolder, IDictionary<string, byte[]> contents, DateTime utcNow, Action rollback, List<string> log,
                                                       Action<string> beforeCreate = null, Func<string, byte[], string> validate = null)
        {
            Dictionary<string, WgsContainerState> states;
            WgsIndex index = ReadChecked(userFolder, contents.Keys, out states);
            byte[] indexBefore = index.ToBytes();
            byte[] indexOnDisk = File.ReadAllBytes(IndexPath(userFolder));
            if (!indexBefore.SequenceEqual(indexOnDisk))
                throw new WgsUnexpectedException("the save index does not read back byte for byte (unknown fields)");

            string fault = Environment.GetEnvironmentVariable(TestFaultVariable) ?? string.Empty;
            long entryTime = utcNow.ToFileTimeUtc() / 10000 * 10000;
            List<string> created = new List<string>();
            Dictionary<string, string> newBlobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WgsContainerState> newStates = new Dictionary<string, WgsContainerState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (KeyValuePair<string, byte[]> content in contents)
                {
                    WgsContainerState state = states[content.Key];
                    Guid fileId = Guid.NewGuid();
                    string blobPath = Path.Combine(state.FolderPath, fileId.ToString("N").ToUpperInvariant());
                    byte next = (byte)(state.Entry.Number >= 255 ? 1 : state.Entry.Number + 1);
                    string containerPath = Path.Combine(state.FolderPath, WgsContainerFile.FileName(next));
                    if (File.Exists(blobPath) || File.Exists(containerPath))
                        throw new WgsUnexpectedException(containerPath + " is already there");
                    // The backup records both as new files (a restore removes them).
                    beforeCreate?.Invoke(blobPath);
                    beforeCreate?.Invoke(containerPath);
                    using (FileStream stream = new FileStream(blobPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        stream.Write(content.Value, 0, content.Value.Length);
                        stream.Flush(true);
                    }
                    created.Add(blobPath);
                    File.SetLastWriteTimeUtc(blobPath, DateTime.FromFileTimeUtc(entryTime));
                    if (fault == "blob")
                        throw new IOException("test fault after the blob");

                    WgsContainerFile container = WgsContainerFile.Parse(state.Container.ToBytes());
                    // The cloud's id stays (libNOM.io does the same): the sync sees a blob it has not uploaded yet.
                    container.Blobs[0].DiskId = fileId;
                    using (FileStream stream = new FileStream(containerPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] bytes = container.ToBytes();
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    created.Add(containerPath);
                    if (fault == "container")
                        throw new IOException("test fault after the container file");

                    WgsIndexEntry entry = state.Entry;
                    entry.Number = next;
                    entry.Size = content.Value.Length;
                    entry.FileTime = entryTime;
                    if (entry.State == WgsIndexEntry.StateSynced)
                        entry.State = WgsIndexEntry.StateModified;
                    newBlobs[content.Key] = blobPath;
                    newStates[content.Key] = new WgsContainerState { Entry = entry, FolderPath = state.FolderPath, ContainerPath = containerPath, Container = container, BlobPath = blobPath };
                    log?.Add(content.Key + ": " + Path.GetFileName(state.ContainerPath) + " -> " + Path.GetFileName(containerPath) + ", blob " + Path.GetFileName(state.BlobPath) + " -> " +
                             Path.GetFileName(blobPath) + " (" + content.Value.Length.ToString(CultureInfo.InvariantCulture) + " bytes), state " + entry.State.ToString(CultureInfo.InvariantCulture));
                }

                // The store's time only grows (the cloud compares it); "fully uploaded" no longer holds.
                index.FileTime = Math.Max(utcNow.ToFileTimeUtc(), index.FileTime + 1);
                index.SyncFlags &= ~WgsIndex.FlagFullyUploaded;
                byte[] indexBytes = index.ToBytes();
                string indexPath = IndexPath(userFolder);
                string temp = indexPath + ".jptmp";
                using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    stream.Write(indexBytes, 0, indexBytes.Length);
                    stream.Flush(true);
                }
                created.Add(temp);
                File.Replace(temp, indexPath, null);
                created.Remove(temp);
                log?.Add("containers.index: time " + DateTime.FromFileTimeUtc(index.FileTime).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z, flags " + index.SyncFlags.ToString(CultureInfo.InvariantCulture));
                if (fault == "index")
                    throw new IOException("test fault after the index");

                // Read back: the index as written, every container and blob as written, the other containers unchanged.
                Dictionary<string, WgsContainerState> reread;
                WgsIndex check = ReadChecked(userFolder, contents.Keys, out reread);
                if (!check.ToBytes().SequenceEqual(indexBytes))
                    throw new WgsUnexpectedException("the save index did not read back as written");
                foreach (KeyValuePair<string, byte[]> content in contents)
                {
                    if (!string.Equals(reread[content.Key].BlobPath, newBlobs[content.Key], StringComparison.OrdinalIgnoreCase) || !File.ReadAllBytes(newBlobs[content.Key]).SequenceEqual(content.Value))
                        throw new WgsUnexpectedException("the new blob of " + content.Key + " did not read back as written");
                    string invalid = validate == null ? null : validate(content.Key, File.ReadAllBytes(newBlobs[content.Key]));
                    if (invalid != null)
                        throw new WgsUnexpectedException("the new blob of " + content.Key + " is not valid: " + invalid);
                }
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    throw;
                foreach (string file in created)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception deleteError) when (deleteError is IOException || deleteError is UnauthorizedAccessException)
                    {
                        log?.Add("Could not remove " + file + ": " + deleteError.Message);
                    }
                }
                rollback?.Invoke();
                throw new WgsWriteFailedException(ex.Message, ex);
            }

            // The superseded generation (the game keeps one container file and one blob per folder).
            foreach (KeyValuePair<string, WgsContainerState> old in states)
            {
                foreach (string file in new[] { old.Value.ContainerPath, old.Value.BlobPath })
                {
                    try
                    {
                        if (!string.Equals(file, newStates[old.Key].ContainerPath, StringComparison.OrdinalIgnoreCase) && !string.Equals(file, newStates[old.Key].BlobPath, StringComparison.OrdinalIgnoreCase))
                            File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log?.Add("Could not remove the superseded " + file + ": " + ex.Message);
                    }
                }
            }
            return newBlobs;
        }
    }

    // Something JoyPro does not expect in the save: nothing was written.
    internal sealed class WgsUnexpectedException : Exception
    {
        public WgsUnexpectedException(string message) : base(message)
        {
        }
    }

    // A write that failed after it started: the backup was put back.
    internal sealed class WgsWriteFailedException : Exception
    {
        public WgsWriteFailedException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
