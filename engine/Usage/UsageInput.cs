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

namespace JoyPro.Usage
{
    // One file a parser reads (a game's file, or a live copy of one). Read only: every open shares read, write and
    // delete, so a game that writes or rotates its log meanwhile is never disturbed.
    public sealed class UsageInput
    {
        public UsageInput(string path, string game, string kind, string displayName = null)
        {
            Path = path;
            Game = game ?? string.Empty;
            Kind = kind ?? string.Empty;
            DisplayName = string.IsNullOrEmpty(displayName) ? System.IO.Path.GetFileName(path) : displayName;
            try
            {
                LastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                LastWriteUtc = DateTime.UtcNow;
            }
        }

        public string Path { get; private set; }
        public string Game { get; private set; }
        public string Kind { get; private set; }
        // The name shown in the statistics (the game's file name, also for a live copy).
        public string DisplayName { get; private set; }
        public DateTime LastWriteUtc { get; set; }

        public Stream Open()
        {
            return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        }

        public IEnumerable<string> Lines(Encoding encoding = null)
        {
            using (Stream stream = Open())
            using (StreamReader reader = new StreamReader(stream, encoding ?? Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    yield return line;
            }
        }

        public string Text(Encoding encoding = null)
        {
            using (Stream stream = Open())
            using (StreamReader reader = new StreamReader(stream, encoding ?? Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        public byte[] Bytes(int max)
        {
            using (Stream stream = Open())
            {
                byte[] buffer = new byte[(int)Math.Min(max, Math.Max(0, stream.Length))];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                if (read < buffer.Length)
                    Array.Resize(ref buffer, read);
                return buffer;
            }
        }

        // SHA-1 of the first bytes (the file's identity when it is renamed or moved; its growth keeps the head).
        public static string HeadHash(string path, int bytes = 4096)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] buffer = new byte[(int)Math.Min(bytes, stream.Length)];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    using (SHA1 sha = SHA1.Create())
                        return BitConverter.ToString(sha.ComputeHash(buffer, 0, read)).Replace("-", string.Empty).Substring(0, 20).ToLowerInvariant();
                }
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

        // The text of a zip entry (Tacview .zip.acmi, DCS .trk), null when there is none.
        public static string ZipEntryText(Stream stream, Func<string, bool> pick, int maxBytes = 64 * 1024 * 1024)
        {
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => pick(e.FullName));
                if (entry == null || entry.Length > maxBytes)
                    return null;
                using (Stream entryStream = entry.Open())
                using (StreamReader reader = new StreamReader(entryStream, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }

        public static bool IsZip(byte[] head)
        {
            return head != null && head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
        }

        // A local time the game wrote (X-Plane, IL-2 file names) as UTC with this PC's time zone.
        public static DateTime LocalToUtc(DateTime local)
        {
            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            }
            catch (ArgumentException)
            {
                return DateTime.SpecifyKind(local, DateTimeKind.Utc);
            }
        }

        public static bool TryDouble(string text, out double value)
        {
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
