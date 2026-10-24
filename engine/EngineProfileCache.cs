using System;
using System.IO;
using System.Threading.Tasks;

namespace JoyPro.Engine
{
    // The profile as read from disk, kept by the engine server (EngineServer) between its commands. Reading a big
    // profile (BinaryFormatter, 4 MB) took 0.4-1.3 s per engine call; every command still gets a profile of its own,
    // exactly as read: a deep copy (ObjectGraphCloner) of the untouched graph read from the file. The file's size and
    // time stamp decide whether the kept graph is still the file's; after the server saved the profile itself, the kept
    // graph is a copy of what it wrote, as a read would give it. Off in a normal engine process.
    internal static class EngineProfileCache
    {
        public static bool Enabled;

        private sealed class Kept
        {
            public string Path;
            public long Length;
            public DateTime WriteUtc;
            public Task<Pr0file> Graph;
        }

        private static readonly object Gate = new object();
        private static Kept kept;

        // Statistics for the server's log line and the self-test.
        public static int Hits;
        public static int Misses;

        private static string Key(string path)
        {
            return System.IO.Path.GetFullPath(path);
        }

        // The profile of filePath for one command: a copy of the kept graph, or read (and kept) now.
        public static Pr0file Load(string filePath, Func<string, Pr0file> read)
        {
            if (!Enabled)
                return read(filePath);
            string key = Key(filePath);
            FileInfo info = new FileInfo(key);
            Kept current;
            lock (Gate)
                current = kept;
            if (current != null && string.Equals(current.Path, key, StringComparison.OrdinalIgnoreCase) &&
                info.Exists && current.Length == info.Length && current.WriteUtc == info.LastWriteTimeUtc)
            {
                Pr0file graph = null;
                try
                {
                    graph = current.Graph.Result;
                }
                catch (AggregateException)
                {
                    graph = null;
                }
                if (graph != null)
                {
                    Hits++;
                    return ObjectGraphCloner.Clone(graph);
                }
            }
            Misses++;
            DateTime writeUtc = info.LastWriteTimeUtc;
            long length = info.Exists ? info.Length : -1;
            Pr0file fresh = read(filePath);
            lock (Gate)
                kept = new Kept { Path = key, Length = length, WriteUtc = writeUtc, Graph = Task.FromResult(fresh) };
            return ObjectGraphCloner.Clone(fresh);
        }

        // The server saved the profile: what a read of the file gives is the written graph read back, a copy of it as
        // BinaryFormatter reads it (ObjectGraphCloner.CloneAsSerialized; engine serve --self-test checks both ways).
        public static void Saved(string filePath, byte[] content, Pr0file written)
        {
            if (!Enabled)
                return;
            try
            {
                string key = Key(filePath);
                FileInfo info = new FileInfo(key);
                if (!info.Exists || info.Length != content.Length)
                {
                    Forget();
                    return;
                }
                // What a read of the file gives: the written graph as BinaryFormatter reads it back (ObjectGraphCloner).
                Kept next = new Kept { Path = key, Length = info.Length, WriteUtc = info.LastWriteTimeUtc, Graph = Task.FromResult(ObjectGraphCloner.CloneAsSerialized(written)) };
                lock (Gate)
                    kept = next;
            }
            catch (Exception)
            {
                Forget();
            }
        }

        public static void Forget()
        {
            lock (Gate)
                kept = null;
        }
    }
}
