using System;
using System.Globalization;
using System.IO;

namespace JoyPro.Engine
{
    // The engine server (EngineServer) keeps the profile as its load correction's DB part left it (renamed and refreshed
    // actions, added aircraft, modifier spellings: InternalDataManagement.ApplyProfile), so the next command that loads
    // the same file with corrections on does not run that part again (0.2-0.4 s on a 4 MB profile; the app runs it at
    // "profile load" and again at the first change after it). Kept per key: the file (path, size, time stamp), the DB
    // fingerprint the correction read, its options and the rules version; any change gives another key, so the
    // correction runs again. What depends on the PC at the moment (the device remap against the connected devices) is
    // never kept: it runs every time. Every command gets a copy of the kept state (ObjectGraphCloner). Off in a normal
    // engine process.
    internal static class EngineLoadCorrectionCache
    {
        public static bool Enabled;

        // Raise when a load correction rule changes what it makes of a profile.
        public const int RulesVersion = 1;

        private sealed class Kept
        {
            public string Key;
            public object State;
        }

        private static readonly object Gate = new object();
        private static Kept kept;

        // Statistics for the self-test and the server's log line.
        public static int Hits;
        public static int Misses;

        // The file part of the key: full path, size and time stamp as the profile was read; empty when it cannot be read.
        public static string FileStamp(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return string.Empty;
                FileInfo info = new FileInfo(Path.GetFullPath(filePath));
                if (!info.Exists)
                    return string.Empty;
                return info.FullName.ToUpperInvariant() + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // The whole key, or null when nothing may be kept (cache off, unknown file or DB fingerprint).
        public static string Key(string fileStamp, string dbFingerprint, string options)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(fileStamp) || string.IsNullOrWhiteSpace(dbFingerprint))
                return null;
            return "v" + RulesVersion.ToString(CultureInfo.InvariantCulture) + "|" + fileStamp + "|" + dbFingerprint + "|" + (options ?? string.Empty);
        }

        // A copy of the kept state for this key, or null.
        public static T TryGet<T>(string key) where T : class
        {
            if (key == null)
                return null;
            Kept current;
            lock (Gate)
                current = kept;
            if (current == null || !string.Equals(current.Key, key, StringComparison.Ordinal))
            {
                Misses++;
                return null;
            }
            Hits++;
            return ObjectGraphCloner.Clone((T)current.State);
        }

        // Keeps a copy of the state (the caller goes on changing its own).
        public static void Keep<T>(string key, T state) where T : class
        {
            if (key == null || state == null)
                return;
            Kept next = new Kept { Key = key, State = ObjectGraphCloner.Clone(state) };
            lock (Gate)
                kept = next;
        }

        public static void Forget()
        {
            lock (Gate)
                kept = null;
        }
    }
}
