using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // Where the sim keeps the user's own input profiles (docs/games/msfs.md "Files"), read only:
    //   - Steam: <Steam>\userdata\<id3>\<app>\remote\inputprofile_* (app 1250410 = 2020, 2537590 = 2024), plain XML.
    //   - Microsoft Store / Xbox app: %LOCALAPPDATA%\Packages\<family>\SystemAppData\wgs\<user>\ (2020:
    //     Microsoft.FlightSimulator_8wekyb3d8bbwe, 2024: Microsoft.Limitless_8wekyb3d8bbwe), the Xbox "connected
    //     storage": containers.index lists the containers, each container folder holds container.<n> (its blob list)
    //     and the blobs, files named by GUID. The input profiles are blobs with the same XML as the Steam files.
    //     The blobs get new names on every save, so JoyPro never writes there.
    internal enum MsfsProfileSourceKind
    {
        // A file or folder the user named (--path), or JoyPro's own preset package (MSFS 2024 Community2024).
        Given,
        Package,
        Steam,
        Store
    }

    internal sealed class MsfsProfileSource
    {
        public MsfsProfileSourceKind Kind;
        // The folder searched (Steam remote folder, wgs folder, package folder).
        public string Folder;
        // The profile files found there (WGS: the blobs that hold an input profile).
        public List<string> Files = new List<string>();
        // Store only: blobs that could not be read as a profile (to say what was found).
        public int OtherBlobs;
    }

    internal sealed class WgsBlob
    {
        public string Container;
        public string Name;
        public string FilePath;
    }

    internal static class MsfsUserProfiles
    {
        // Tests: a folder that stands in for the user's folders (<home>\Local = %LOCALAPPDATA%, <home>\Roaming =
        // %APPDATA%, <home>\Steam = the Steam install), so no real profile is read.
        public const string TestHomeVariable = "JOYPRO_TEST_MSFS_HOME";

        public static string StorePackageFamily(int sim)
        {
            return sim == 2020 ? "Microsoft.FlightSimulator_8wekyb3d8bbwe" : "Microsoft.Limitless_8wekyb3d8bbwe";
        }

        public static string SteamApp(int sim)
        {
            return sim == 2020 ? "1250410" : "2537590";
        }

        private static string TestHome()
        {
            string home = Environment.GetEnvironmentVariable(TestHomeVariable);
            return string.IsNullOrWhiteSpace(home) ? null : home.Trim();
        }

        public static bool UsesTestHome
        {
            get { return TestHome() != null; }
        }

        public static string LocalAppData()
        {
            string home = TestHome();
            return home != null ? Path.Combine(home, "Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        public static string RoamingAppData()
        {
            string home = TestHome();
            return home != null ? Path.Combine(home, "Roaming") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        public static List<string> SteamRoots(List<string> warnings)
        {
            string home = TestHome();
            if (home != null)
                return new List<string> { Path.Combine(home, "Steam") };
            return GamePathDiscovery.GetSteamInstallRoots(warnings);
        }

        // Every Steam remote folder of the sim (whether it has profiles or not; the caller filters).
        public static List<string> SteamRemoteFolders(int sim, List<string> warnings)
        {
            List<string> folders = new List<string>();
            foreach (string steam in SteamRoots(warnings))
            {
                string userdata = Path.Combine(steam, "userdata");
                if (!Directory.Exists(userdata))
                    continue;
                try
                {
                    foreach (string user in Directory.GetDirectories(userdata))
                    {
                        string remote = Path.Combine(user, SteamApp(sim), "remote");
                        if (Directory.Exists(remote))
                            folders.Add(remote);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return folders;
        }

        public static string StorePackageFolder(int sim)
        {
            return Path.Combine(LocalAppData(), "Packages", StorePackageFamily(sim));
        }

        // %LOCALAPPDATA%\Packages\<family>\SystemAppData\wgs (exists only for a Store / Xbox app install).
        public static string StoreWgsFolder(int sim)
        {
            return Path.Combine(StorePackageFolder(sim), "SystemAppData", "wgs");
        }

        public static List<string> SteamProfileFiles(string remote)
        {
            try
            {
                return Directory.GetFiles(remote, "inputprofile_*").Where(f => !f.EndsWith(".jpbak", StringComparison.OrdinalIgnoreCase) && MsfsProfileFile.LooksLikeProfile(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new List<string>();
        }

        // The user's own profiles of one sim: every Steam remote folder and the Store's wgs folder, with the files
        // found (sources without files are kept, so a message can name every folder that was searched).
        public static List<MsfsProfileSource> UserSources(int sim, List<string> warnings)
        {
            List<MsfsProfileSource> sources = new List<MsfsProfileSource>();
            foreach (string remote in SteamRemoteFolders(sim, warnings))
                sources.Add(new MsfsProfileSource { Kind = MsfsProfileSourceKind.Steam, Folder = remote, Files = SteamProfileFiles(remote) });
            string wgs = StoreWgsFolder(sim);
            if (Directory.Exists(wgs))
                sources.Add(StoreSource(wgs, warnings));
            return sources;
        }

        public static MsfsProfileSource StoreSource(string wgsFolder, List<string> warnings)
        {
            MsfsProfileSource source = new MsfsProfileSource { Kind = MsfsProfileSourceKind.Store, Folder = wgsFolder };
            foreach (WgsBlob blob in WgsStore.Blobs(wgsFolder, warnings))
            {
                string profile = ProfileFileOf(blob.FilePath);
                if (profile != null)
                    source.Files.Add(profile);
                else
                    source.OtherBlobs++;
            }
            return source;
        }

        // The folders a search looks at for one sim, for the "nothing found" message (existing or not).
        public static List<string> SearchedFolders(int sim, List<string> warnings)
        {
            List<string> folders = new List<string>();
            List<string> remotes = SteamRemoteFolders(sim, warnings);
            if (remotes.Count > 0)
                folders.AddRange(remotes);
            else
                folders.Add("<Steam>\\userdata\\<id>\\" + SteamApp(sim) + "\\remote (no Steam folder of the sim)");
            folders.Add(StoreWgsFolder(sim));
            return folders;
        }

        // A blob or file that holds an input profile: its path, or for a compressed blob a decompressed copy in
        // JoyPro's cache. Null when it is no profile.
        private static string ProfileFileOf(string path)
        {
            if (MsfsProfileFile.LooksLikeProfile(path))
                return path;
            byte[] head = new byte[2];
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < 16 || stream.Length > 16 * 1024 * 1024 || stream.Read(head, 0, 2) != 2)
                        return null;
                    bool gzip = head[0] == 0x1F && head[1] == 0x8B;
                    bool zlib = head[0] == 0x78 && (head[1] == 0x01 || head[1] == 0x5E || head[1] == 0x9C || head[1] == 0xDA);
                    if (!gzip && !zlib)
                        return null;
                    stream.Position = gzip ? 0 : 2;
                    using (Stream inflater = gzip ? (Stream)new GZipStream(stream, CompressionMode.Decompress, true) : new DeflateStream(stream, CompressionMode.Decompress, true))
                    using (MemoryStream output = new MemoryStream())
                    {
                        byte[] buffer = new byte[65536];
                        int read;
                        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            if (output.Length > 32 * 1024 * 1024)
                                return null;
                        }
                        string text = DecodeText(output.ToArray());
                        if (!(text.Contains("<Device ") && (text.Contains("<Version") || text.Contains("<FriendlyName"))))
                            return null;
                        string cache = Path.Combine(CacheFolder(), Path.GetFileName(Path.GetDirectoryName(path)) + "_" + Path.GetFileName(path) + ".xml");
                        File.WriteAllText(cache, text, new UTF8Encoding(false));
                        File.SetLastWriteTimeUtc(cache, File.GetLastWriteTimeUtc(path));
                        return cache;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
            return null;
        }

        internal static string DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        private static string CacheFolder()
        {
            string folder = Path.Combine(GameInstalls.DataRoot(), "Cache", "MSFS", "wgs");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static bool IsStorePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string full = path.Replace('/', '\\');
            return full.IndexOf("\\SystemAppData\\wgs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   full.IndexOf(Path.Combine("Cache", "MSFS", "wgs"), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A wgs folder for a path the user gave: the wgs folder itself, a user folder or container folder in it, or
        // the Store package folder (Packages\<family>).
        public static string WgsFolderOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return null;
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string direct = Path.Combine(full, "SystemAppData", "wgs");
            if (Directory.Exists(direct))
                return direct;
            int index = full.IndexOf("\\SystemAppData\\wgs", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return full.Substring(0, index + "\\SystemAppData\\wgs".Length);
            if (string.Equals(Path.GetFileName(full), "wgs", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(full, "containers.index")))
                return full;
            return null;
        }
    }

    // The Xbox connected-storage layout ("wgs") of Microsoft Store games (format as documented by the save tools of
    // Store games, e.g. XGP-save-extractor):
    //   <wgs>\<user id>_<title id>\containers.index:
    //     uint32 version, int32 container count, uint32, string package name ("<family>!App"), uint64 FILETIME,
    //     uint32, string, uint64; then per container: string name, string name again, string (quoted hex id),
    //     byte container file number, uint32, GUID folder, uint64 FILETIME, 16 bytes.
    //   <user folder>\<folder GUID, 32 hex digits>\container.<number>:
    //     uint32 (4), int32 blob count; per blob: name (64 UTF-16 characters, NUL padded), GUID file, GUID again.
    //   The blob is the file <folder>\<file GUID, 32 hex digits>.
    //   Strings: int32 length in characters, then UTF-16LE.
    // When the index cannot be read (another version of the format), every file named like a blob is offered instead.
    internal static class WgsStore
    {
        private static readonly Regex HexName = new Regex("^[0-9A-Fa-f]{32}$", RegexOptions.Compiled);

        public static List<WgsBlob> Blobs(string wgsFolder, List<string> warnings)
        {
            List<WgsBlob> blobs = new List<WgsBlob>();
            if (string.IsNullOrWhiteSpace(wgsFolder) || !Directory.Exists(wgsFolder))
                return blobs;
            List<string> userFolders = new List<string>();
            if (File.Exists(Path.Combine(wgsFolder, "containers.index")))
                userFolders.Add(wgsFolder);
            else
            {
                try
                {
                    userFolders.AddRange(Directory.GetDirectories(wgsFolder).Where(d => !string.Equals(Path.GetFileName(d), "t", StringComparison.OrdinalIgnoreCase)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string user in userFolders)
            {
                List<WgsBlob> listed = null;
                string index = Path.Combine(user, "containers.index");
                if (File.Exists(index))
                {
                    try
                    {
                        listed = ReadIndex(user, index);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException)
                            throw;
                        warnings?.Add("Could not read the Microsoft Store save index " + index + " (" + ex.Message + "); every saved file there is checked instead.");
                        listed = null;
                    }
                }
                // An index that names no file that is there (another layout of the format) counts as unreadable.
                if (listed == null || !listed.Any(b => File.Exists(b.FilePath)))
                    listed = ScanFolder(user);
                blobs.AddRange(listed.Where(b => File.Exists(b.FilePath)));
            }
            return blobs;
        }

        // Every file named like a blob in the container folders of a user folder.
        private static List<WgsBlob> ScanFolder(string user)
        {
            List<WgsBlob> blobs = new List<WgsBlob>();
            try
            {
                foreach (string container in Directory.GetDirectories(user))
                {
                    foreach (string file in Directory.GetFiles(container))
                    {
                        if (HexName.IsMatch(Path.GetFileName(file)))
                            blobs.Add(new WgsBlob { Container = Path.GetFileName(container), Name = Path.GetFileName(file), FilePath = file });
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return blobs;
        }

        private static List<WgsBlob> ReadIndex(string user, string indexPath)
        {
            List<WgsBlob> blobs = new List<WgsBlob>();
            using (BinaryReader reader = new BinaryReader(new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.Unicode))
            {
                reader.ReadUInt32();
                int count = reader.ReadInt32();
                if (count < 0 || count > 100000)
                    throw new InvalidDataException("container count " + count.ToString(CultureInfo.InvariantCulture));
                reader.ReadUInt32();
                ReadString(reader);
                reader.ReadUInt64();
                reader.ReadUInt32();
                ReadString(reader);
                reader.ReadUInt64();
                for (int i = 0; i < count; i++)
                {
                    string name = ReadString(reader);
                    ReadString(reader);
                    ReadString(reader);
                    byte number = reader.ReadByte();
                    reader.ReadUInt32();
                    Guid folder = new Guid(reader.ReadBytes(16));
                    reader.ReadUInt64();
                    reader.ReadBytes(16);
                    string containerFolder = Path.Combine(user, folder.ToString("N").ToUpperInvariant());
                    string containerFile = Path.Combine(containerFolder, "container." + number.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(containerFile))
                        continue;
                    blobs.AddRange(ReadContainer(name, containerFolder, containerFile));
                }
            }
            return blobs;
        }

        private static List<WgsBlob> ReadContainer(string container, string folder, string containerFile)
        {
            List<WgsBlob> blobs = new List<WgsBlob>();
            using (BinaryReader reader = new BinaryReader(new FileStream(containerFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            {
                reader.ReadUInt32();
                int count = reader.ReadInt32();
                if (count < 0 || count > 100000)
                    throw new InvalidDataException("blob count " + count.ToString(CultureInfo.InvariantCulture) + " in " + containerFile);
                for (int i = 0; i < count; i++)
                {
                    string name = Encoding.Unicode.GetString(reader.ReadBytes(128)).TrimEnd('\0');
                    int nul = name.IndexOf('\0');
                    if (nul >= 0)
                        name = name.Substring(0, nul);
                    Guid file = new Guid(reader.ReadBytes(16));
                    reader.ReadBytes(16);
                    blobs.Add(new WgsBlob { Container = container, Name = name, FilePath = Path.Combine(folder, file.ToString("N").ToUpperInvariant()) });
                }
            }
            return blobs;
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 4096)
                throw new InvalidDataException("string length " + length.ToString(CultureInfo.InvariantCulture));
            return Encoding.Unicode.GetString(reader.ReadBytes(length * 2)).TrimEnd('\0');
        }
    }
}
