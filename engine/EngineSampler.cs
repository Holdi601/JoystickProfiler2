using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JoyPro.Engine
{
    // A small sampling profiler for development (JOYPRO_SAMPLE_OUT=<file>): every few milliseconds the main thread's stack
    // is taken and counted; at the end of the run the methods with the most samples (inclusive and on top) are written to
    // the file. Off unless the variable is set.
    internal static class EngineSampler
    {
        private static Thread sampler;
        private static volatile bool stop;
        private static readonly Dictionary<string, int> Inclusive = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Exclusive = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Paths = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int samples;
        private static string outPath;
        private static Stopwatch clock;

        public static void StartIfAsked()
        {
            outPath = Environment.GetEnvironmentVariable("JOYPRO_SAMPLE_OUT");
            if (string.IsNullOrWhiteSpace(outPath))
                return;
            Thread target = Thread.CurrentThread;
            clock = Stopwatch.StartNew();
            sampler = new Thread(() => Loop(target)) { IsBackground = true, Name = "sampler" };
            sampler.Start();
            // A stack walk can wait for a lock the suspended thread holds: the watchdog lets it go on after 30 ms.
            new Thread(() =>
            {
                while (!stop)
                {
                    Thread.Sleep(5);
                    long at = Interlocked.Read(ref suspendedAt);
                    if (at > 0 && clock.ElapsedMilliseconds - at > 30)
                    {
                        Interlocked.Exchange(ref suspendedAt, 0);
                        try { target.Resume(); } catch (Exception) { }
                    }
                }
            }) { IsBackground = true, Name = "sampler-watchdog" }.Start();
        }

        private static long suspendedAt;

#pragma warning disable 618
        private static void Loop(Thread target)
        {
            while (!stop)
            {
                Thread.Sleep(2);
                StackTrace trace = null;
                bool suspended = false;
                try
                {
                    target.Suspend();
                    suspended = true;
                    Interlocked.Exchange(ref suspendedAt, Math.Max(1, clock.ElapsedMilliseconds));
                    trace = new StackTrace(target, false);
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (suspended && Interlocked.Exchange(ref suspendedAt, 0) > 0)
                    {
                        try { target.Resume(); } catch (Exception) { }
                    }
                }
                if (trace == null)
                    continue;
                samples++;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                StackFrame[] frames = trace.GetFrames() ?? new StackFrame[0];
                List<string> names = new List<string>();
                for (int i = 0; i < frames.Length; i++)
                {
                    System.Reflection.MethodBase m = frames[i].GetMethod();
                    string name = m == null ? "?" : (m.DeclaringType == null ? "" : m.DeclaringType.Name + ".") + m.Name;
                    names.Add(name);
                    if (i == 0)
                        Exclusive[name] = (Exclusive.TryGetValue(name, out int e) ? e : 0) + 1;
                    if (seen.Add(name))
                        Inclusive[name] = (Inclusive.TryGetValue(name, out int c) ? c : 0) + 1;
                }
                // The innermost engine frames (JoyPro code) as a short path.
                string path = string.Join(" < ", names.Where(n => !n.StartsWith("?")).Take(6));
                Paths[path] = (Paths.TryGetValue(path, out int p) ? p : 0) + 1;
            }
        }
#pragma warning restore 618

        public static void Finish()
        {
            if (sampler == null)
                return;
            stop = true;
            sampler.Join(1000);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("samples " + samples + " over " + clock.ElapsedMilliseconds + " ms");
            sb.AppendLine("== inclusive");
            foreach (KeyValuePair<string, int> kv in Inclusive.OrderByDescending(k => k.Value).Take(120))
                sb.AppendLine(kv.Value.ToString().PadLeft(6) + "  " + kv.Key);
            sb.AppendLine("== exclusive");
            foreach (KeyValuePair<string, int> kv in Exclusive.OrderByDescending(k => k.Value).Take(50))
                sb.AppendLine(kv.Value.ToString().PadLeft(6) + "  " + kv.Key);
            sb.AppendLine("== paths");
            foreach (KeyValuePair<string, int> kv in Paths.OrderByDescending(k => k.Value).Take(40))
                sb.AppendLine(kv.Value.ToString().PadLeft(6) + "  " + kv.Key);
            File.WriteAllText(outPath, sb.ToString());
        }
    }
}
