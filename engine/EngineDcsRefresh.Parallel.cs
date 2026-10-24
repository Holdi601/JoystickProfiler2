using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace JoyPro.Engine
{
    // The headless DCS refresh (dcs refresh id / clean) runs DCS's Lua in several luae.exe processes at once: one run
    // with every aircraft took minutes (each loaded profile slows the next one down, and a run with all fake sticks
    // needed more than 4 GB). Every run lists all profile candidates, so DCS merges and aliases them the same way in
    // every run, and works on its share: a few aircraft, or DCS's default profiles (General, UiLayer, ...), which one
    // run of its own handles. The number of runs at once is bounded by the CPU count and the free memory.
    //   JOYPRO_DCS_LUA_WORKERS=<n>: at most n runs at once (1 = one after the other).
    //   JOYPRO_DCS_LUA_CHUNK=<n>: n aircraft per run.
    public static partial class EngineDcsRefresh
    {
        internal const string LuaWorkersVariable = "JOYPRO_DCS_LUA_WORKERS";
        internal const string LuaChunkVariable = "JOYPRO_DCS_LUA_CHUNK";
        private const int MaxLuaWorkers = 16;
        private const int MaxFakeParts = 8;
        private const int LuaRunAttempts = 3;
        // Memory one run may need (MB): an ID run of a few aircraft stays below 500 MB, a clean run with ~60 fake
        // sticks below 1 GB.
        private const long IdRunMemoryMb = 700;
        private const long CleanRunMemoryMb = 900;
        private const long KeepFreeMemoryMb = 2048;
        private const int LuaRunTimeoutMs = 15 * 60 * 1000;

        private sealed class DcsLuaJob
        {
            public int Order;
            public string Name = string.Empty;
            public List<DcsHeadlessProfileCandidate> Assigned = new List<DcsHeadlessProfileCandidate>();
            public List<int> AssignedIndexes = new List<int>();
            public bool WithDefaults;
            public string SavedGames = string.Empty;
            public string ExportPath;
            public List<string> DeviceTemplates;
            public string ScriptPath = string.Empty;
            public double Weight;
            public int FakePart;
            public int FakeParts = 1;
            public Dictionary<int, double> Costs = new Dictionary<int, double>();
            // The runs that replaced this one after it failed (RunLuaJobs' retry).
            public List<DcsLuaJob> Retries = new List<DcsLuaJob>();
            public string LastProgress = string.Empty;

            public bool Ok;
            public int Succeeded;
            public int Failed;
            public bool SawDone;
            public int[] DoneSummary;
            public string DeviceCount;
            public string NamesCount;
            public bool FastClear;
            public List<string> Templates = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class DcsLuaPlan
        {
            public List<DcsLuaJob> Jobs = new List<DcsLuaJob>();
            public int Workers;
            public int ChunkSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        private static long AvailablePhysicalMemoryMb()
        {
            try
            {
                MemoryStatusEx status = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(status))
                    return (long)(status.ullAvailPhys / (1024 * 1024));
            }
            catch (Exception)
            {
            }
            return -1;
        }

        private static int PositiveEnvironmentInt(string name)
        {
            int value;
            return int.TryParse(Environment.GetEnvironmentVariable(name) ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0 ? value : 0;
        }

        // How many luae.exe may run at once: one CPU stays free, and every run gets its memory share of what is free now.
        private static int LuaWorkerBound(long perRunMb)
        {
            int forced = PositiveEnvironmentInt(LuaWorkersVariable);
            if (forced > 0)
                return Math.Min(forced, 64);
            int cpu = Math.Max(1, Environment.ProcessorCount - 1);
            long available = AvailablePhysicalMemoryMb();
            int memory = available <= 0 ? 4 : (int)Math.Max(1, (available - KeepFreeMemoryMb) / perRunMb);
            return Math.Max(1, Math.Min(MaxLuaWorkers, Math.Min(cpu, memory)));
        }

        // The runs of a refresh: DCS's default profiles in a run of their own, then the aircraft. The clean refresh gives
        // every aircraft a run of its own (a run starts in 0.3 s, one aircraft with ~100 fake sticks takes up to a
        // minute and a half); the ID refresh packs a few aircraft into a run (starting a run costs about a second there),
        // about three runs per worker. The aircraft that took longest last time (LoadLuaCosts) start first, so a slow
        // one does not hold up the end. The jobs come in the order they start; Order is the merge order (aircraft order).
        private static DcsLuaPlan PlanLuaJobs(List<DcsHeadlessProfileCandidate> candidates, bool exportHtml, Dictionary<string, double> costs = null)
        {
            DcsLuaPlan plan = new DcsLuaPlan();
            int count = candidates.Count;
            int bound = LuaWorkerBound(exportHtml ? IdRunMemoryMb : CleanRunMemoryMb);
            DcsLuaJob defaults = new DcsLuaJob { Order = 0, Name = "DCS default profiles", WithDefaults = true };
            List<List<int>> runs = new List<List<int>>();
            int forced = PositiveEnvironmentInt(LuaChunkVariable);
            double[] weights = new double[count];
            if (forced > 0)
            {
                plan.ChunkSize = forced;
                for (int start = 0; start < count; start += forced)
                    runs.Add(Enumerable.Range(start, Math.Min(forced, count - start)).ToList());
            }
            else
            {
                List<double> known = new List<double>();
                for (int i = 0; i < count; i++)
                {
                    double cost;
                    if (costs != null && costs.TryGetValue(candidates[i].UnitName ?? string.Empty, out cost) && cost > 0)
                    {
                        weights[i] = cost;
                        known.Add(cost);
                    }
                }
                double fallback = known.Count > 0 ? known.Average() : 1.0;
                for (int i = 0; i < count; i++)
                {
                    if (weights[i] <= 0)
                        weights[i] = fallback;
                }
                int perRun = exportHtml ? 6 : 1;
                double target = weights.Sum() / (bound * 3.0);
                List<double> loads = new List<double>();
                // First fit, heaviest first.
                foreach (int i in Enumerable.Range(0, count).OrderByDescending(i => weights[i]).ThenBy(i => i))
                {
                    int run = -1;
                    for (int r = 0; r < runs.Count; r++)
                    {
                        if (runs[r].Count < perRun && loads[r] + weights[i] <= target)
                        {
                            run = r;
                            break;
                        }
                    }
                    if (run < 0)
                    {
                        runs.Add(new List<int>());
                        loads.Add(0);
                        run = runs.Count - 1;
                    }
                    runs[run].Add(i);
                    loads[run] += weights[i];
                }
                plan.ChunkSize = runs.Count == 0 ? 1 : runs.Max(r => r.Count);
            }

            List<DcsLuaJob> jobs = new List<DcsLuaJob>();
            foreach (List<int> run in runs.OrderBy(r => r.Min()))
            {
                DcsLuaJob job = new DcsLuaJob { Order = (jobs.Count + 1) * 1000, WithDefaults = false };
                foreach (int i in run.OrderBy(i => i))
                {
                    job.Assigned.Add(candidates[i]);
                    job.AssignedIndexes.Add(i + 1);
                    job.Weight += weights[i];
                }
                job.Name = job.Assigned[0].UnitName + (job.Assigned.Count > 1 ? " .. " + job.Assigned[job.Assigned.Count - 1].UnitName : string.Empty);
                jobs.Add(job);
            }
            // Clean: an aircraft slower than a worker's share of the whole is cleared by several runs, each with a part of
            // the fake sticks (at most 8).
            if (!exportHtml && forced <= 0 && jobs.Count > 0)
            {
                double share = weights.Sum() / bound;
                List<DcsLuaJob> split = new List<DcsLuaJob>();
                foreach (DcsLuaJob job in jobs)
                {
                    int parts = share <= 0 ? 1 : Math.Min(MaxFakeParts, (int)Math.Ceiling(job.Weight / share));
                    if (parts <= 1 || job.Assigned.Count != 1)
                    {
                        split.Add(job);
                        continue;
                    }
                    for (int part = 0; part < parts; part++)
                    {
                        DcsLuaJob piece = new DcsLuaJob
                        {
                            Order = job.Order + part * 100,
                            WithDefaults = false,
                            FakePart = part,
                            FakeParts = parts,
                            Weight = job.Weight / parts,
                            Name = job.Name + " (fake sticks " + (part + 1).ToString(CultureInfo.InvariantCulture) + "/" + parts.ToString(CultureInfo.InvariantCulture) + ")"
                        };
                        piece.Assigned.AddRange(job.Assigned);
                        piece.AssignedIndexes.AddRange(job.AssignedIndexes);
                        split.Add(piece);
                    }
                }
                jobs = split;
            }
            // The default profiles first, then the slowest runs.
            plan.Jobs.Add(defaults);
            plan.Jobs.AddRange(jobs.OrderByDescending(j => j.Weight).ThenBy(j => j.Order));
            plan.Workers = Math.Max(1, Math.Min(bound, plan.Jobs.Count));
            return plan;
        }

        // ---- What each aircraft took last time (<data>\Cache\dcs-refresh-times.tsv: "<id|clean>\t<unit>\t<seconds>") ----

        private const string LuaCostsFile = "dcs-refresh-times.tsv";

        private static string LuaCostsPath()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Cache", LuaCostsFile);
        }

        private static List<string[]> ReadLuaCostLines()
        {
            List<string[]> lines = new List<string[]>();
            try
            {
                string path = LuaCostsPath();
                if (!File.Exists(path))
                    return lines;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length == 3 && (parts[0] == "id" || parts[0] == "clean"))
                        lines.Add(parts);
                }
            }
            catch (Exception)
            {
            }
            return lines;
        }

        // The seconds per aircraft of this refresh kind, else of the other one (the ID refresh runs before the first
        // clean refresh; the same aircraft are slow in both). Empty without a previous refresh.
        private static Dictionary<string, double> LoadLuaCosts(bool exportHtml)
        {
            Dictionary<string, double> own = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> other = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string mode = exportHtml ? "id" : "clean";
            foreach (string[] parts in ReadLuaCostLines())
            {
                double seconds;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                    continue;
                (parts[0] == mode ? own : other)[parts[1]] = seconds;
            }
            return own.Count > 0 ? own : other;
        }

        private static void SaveLuaCosts(bool exportHtml, IEnumerable<DcsLuaJob> jobs, List<DcsHeadlessProfileCandidate> candidates)
        {
            try
            {
                string mode = exportHtml ? "id" : "clean";
                Dictionary<string, double> measured = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (DcsLuaJob job in jobs)
                {
                    foreach (KeyValuePair<int, double> cost in job.Costs)
                    {
                        if (cost.Key >= 1 && cost.Key <= candidates.Count)
                        {
                            // A slow aircraft cleared by several runs: the sum of its parts.
                            string unit = candidates[cost.Key - 1].UnitName ?? string.Empty;
                            double before;
                            measured.TryGetValue(unit, out before);
                            measured[unit] = before + cost.Value;
                        }
                    }
                }
                if (measured.Count == 0)
                    return;
                StringBuilder text = new StringBuilder();
                foreach (string[] parts in ReadLuaCostLines())
                {
                    if (parts[0] == mode && measured.ContainsKey(parts[1]))
                        continue;
                    text.Append(parts[0]).Append('\t').Append(parts[1]).Append('\t').Append(parts[2]).Append("\r\n");
                }
                foreach (KeyValuePair<string, double> cost in measured.OrderBy(c => c.Key, StringComparer.Ordinal))
                    text.Append(mode).Append('\t').Append(cost.Key).Append('\t').Append(cost.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append("\r\n");
                string path = LuaCostsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("DcsRefresh", "Could not save the refresh times: " + ex.Message);
            }
        }

        // Single-aircraft runs for a run that failed (luae.exe can crash on one module): they take its place in the order.
        private static List<DcsLuaJob> SplitLuaJob(DcsLuaJob failed)
        {
            List<DcsLuaJob> singles = new List<DcsLuaJob>();
            for (int i = 0; i < failed.Assigned.Count; i++)
            {
                DcsLuaJob single = new DcsLuaJob { Order = failed.Order + i + 1, WithDefaults = false, Name = failed.Name, FakePart = failed.FakePart, FakeParts = failed.FakeParts };
                single.Assigned.Add(failed.Assigned[i]);
                single.AssignedIndexes.Add(failed.AssignedIndexes[i]);
                singles.Add(single);
            }
            return singles;
        }

        // Runs the jobs, at most `workers` at once. progress gets every JOYPRO_PROGRESS line (from several threads).
        // A cancel (EngineProgress) stops every luae.exe and ends with EngineCancelledException; false when a run failed
        // (its Errors say why), true otherwise.
        private static bool RunLuaJobs(
            DcsRefreshOptions options,
            DcsRefreshResult result,
            List<DcsHeadlessProfileCandidate> allCandidates,
            List<DcsLuaJob> jobs,
            int workers,
            bool exportHtml,
            Action<DcsLuaJob, string, int, int, string> progress,
            string what,
            Func<DcsLuaJob, List<DcsLuaJob>> retry = null)
        {
            string modeLabel = exportHtml ? "ID" : "clean";
            string luaePath = ResolveLuaExecutable(options);
            if (string.IsNullOrWhiteSpace(luaePath) || !File.Exists(luaePath))
            {
                result.Errors.Add("luae.exe was not found for headless " + modeLabel + " refresh. Expected under DCS install bin folder.");
                return false;
            }
            string workingDirectory = NormalizePath(options.InstallPath);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                result.Errors.Add("DCS install path is missing for headless " + modeLabel + " refresh.");
                return false;
            }
            if (jobs.Count == 0 || allCandidates.Count == 0)
            {
                result.Errors.Add("No DCS profile candidates were selected for headless " + modeLabel + " refresh.");
                return false;
            }

            try
            {
                foreach (DcsLuaJob job in jobs)
                {
                    string script = BuildHeadlessLuaRefreshScript(job.SavedGames, allCandidates, exportHtml, job.DeviceTemplates, job.AssignedIndexes, job.WithDefaults, job.ExportPath, job.FakePart, job.FakeParts);
                    job.ScriptPath = Path.Combine(Path.GetTempPath(), "joypro_dcs_headless_refresh_" + (exportHtml ? "id_" : "clean_") + Guid.NewGuid().ToString("N") + ".lua");
                    // Lua 5.1 opens files with the ANSI code page, so embedded paths must use it too.
                    File.WriteAllText(job.ScriptPath, script, Encoding.Default);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing headless Lua " + modeLabel + " refresh script: " + ex.Message);
                DeleteLuaScripts(jobs, result);
                return false;
            }

            ReportStatus(options, "Headless Lua " + modeLabel + " refresh: " + jobs.Count.ToString(CultureInfo.InvariantCulture) + " luae.exe run(s), " +
                                  Math.Min(workers, jobs.Count).ToString(CultureInfo.InvariantCulture) + " at once.");
            int next = -1;
            int abort = 0;
            EngineCancelledException cancelled = null;
            object gate = new object();
            ThreadStart work = () =>
            {
                while (Volatile.Read(ref abort) == 0)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= jobs.Count)
                        return;
                    DcsLuaJob job = jobs[index];
                    try
                    {
                        RunLuaJob(luaePath, workingDirectory, job, exportHtml, progress, what, () => Volatile.Read(ref abort) != 0);
                        // luae.exe sometimes crashes (0xC000041D) or fails on one module: the run is repeated at once
                        // by this worker (retry: the replacement runs, e.g. one aircraft each), without its progress.
                        // A replacement that fails is repeated too, up to LuaRunAttempts runs per aircraft in all; a crash
                        // comes now and then, a module that cannot be cleared fails every time.
                        List<DcsLuaJob> failedRuns = new List<DcsLuaJob> { job };
                        for (int attempt = 1; attempt < LuaRunAttempts && retry != null && Volatile.Read(ref abort) == 0; attempt++)
                        {
                            List<DcsLuaJob> stillFailed = new List<DcsLuaJob>();
                            foreach (DcsLuaJob failedRun in failedRuns.Where(r => !r.Ok))
                            foreach (DcsLuaJob again in retry(failedRun))
                            {
                                job.Retries.Remove(failedRun);
                                job.Retries.Add(again);
                                stillFailed.Add(again);
                                again.ScriptPath = Path.Combine(Path.GetTempPath(), "joypro_dcs_headless_refresh_retry_" + Guid.NewGuid().ToString("N") + ".lua");
                                try
                                {
                                    File.WriteAllText(again.ScriptPath, BuildHeadlessLuaRefreshScript(again.SavedGames, allCandidates, exportHtml, again.DeviceTemplates, again.AssignedIndexes, again.WithDefaults, again.ExportPath, again.FakePart, again.FakeParts), Encoding.Default);
                                    RunLuaJob(luaePath, workingDirectory, again, exportHtml, null, what, () => Volatile.Read(ref abort) != 0);
                                }
                                finally
                                {
                                    try { File.Delete(again.ScriptPath); } catch (Exception) { }
                                }
                            }
                            failedRuns = stillFailed;
                            if (failedRuns.All(r => r.Ok))
                                break;
                        }
                    }
                    catch (EngineCancelledException ex)
                    {
                        lock (gate)
                        {
                            if (cancelled == null)
                                cancelled = ex;
                        }
                        Interlocked.Exchange(ref abort, 1);
                    }
                    catch (Exception ex)
                    {
                        job.Errors.Add("Headless Lua " + modeLabel + " refresh failed: " + ex.Message);
                    }
                }
            };
            try
            {
                int threadCount = Math.Max(1, Math.Min(workers, jobs.Count));
                List<Thread> threads = new List<Thread>();
                for (int i = 0; i < threadCount; i++)
                {
                    Thread thread = new Thread(work) { IsBackground = true, Name = "luae run " + (i + 1).ToString(CultureInfo.InvariantCulture) };
                    threads.Add(thread);
                    thread.Start();
                }
                foreach (Thread thread in threads)
                    thread.Join();
            }
            finally
            {
                DeleteLuaScripts(jobs, result);
            }
            if (cancelled != null)
                throw new EngineCancelledException(cancelled.Message);
            EngineProgress.ThrowIfCancelled(what);
            return jobs.All(j => j.Ok);
        }

        private static void DeleteLuaScripts(List<DcsLuaJob> jobs, DcsRefreshResult result)
        {
            foreach (DcsLuaJob job in jobs)
            {
                try
                {
                    if (!string.IsNullOrEmpty(job.ScriptPath) && File.Exists(job.ScriptPath))
                        File.Delete(job.ScriptPath);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Failed deleting temporary headless refresh script: " + ex.Message);
                }
            }
        }

        // One luae.exe run: its output lines go into the job (thread-safe per job; jobs share nothing but progress).
        private static void RunLuaJob(string luaePath, string workingDirectory, DcsLuaJob job, bool exportHtml,
            Action<DcsLuaJob, string, int, int, string> progress, string what, Func<bool> aborted)
        {
            string modeLabel = exportHtml ? "ID" : "clean";
            List<string> outputLines = new List<string>();
            List<string> errorLines = new List<string>();
            object outputLock = new object();
            ProcessStartInfo startInfo = new ProcessStartInfo(luaePath)
            {
                Arguments = "\"" + job.ScriptPath + "\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
                CreateNoWindow = true
            };
            EngineProgress.ThrowIfCancelled(what);
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args == null || args.Data == null)
                        return;
                    // "JOYPRO_PROGRESS|<i>|<n>|<stage>|<profile>": the profile luae works on now.
                    if (args.Data.StartsWith("JOYPRO_PROGRESS|", StringComparison.Ordinal))
                    {
                        string[] parts = args.Data.Split(new[] { '|' }, 5);
                        if (parts.Length >= 5)
                            job.LastProgress = parts[3] + " " + parts[4] + " (" + parts[1] + "/" + parts[2] + ")";
                        int index;
                        int count;
                        if (progress != null && parts.Length >= 5 &&
                            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                            progress(job, parts[3], index, count, parts[4]);
                        return;
                    }
                    lock (outputLock)
                        outputLines.Add(args.Data);
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args != null && args.Data != null)
                    {
                        lock (outputLock)
                            errorLines.Add(args.Data);
                    }
                };
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    job.Errors.Add("Failed launching luae.exe for headless " + modeLabel + " refresh: " + ex.Message);
                    return;
                }
                // Below normal priority: the PC stays usable while the runs take every core.
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception)
                {
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Stopwatch clock = Stopwatch.StartNew();
                while (!process.WaitForExit(250))
                {
                    bool cancel = EngineProgress.CancelRequested;
                    if (cancel || aborted())
                    {
                        StopLuaProcess(process);
                        if (cancel)
                            EngineProgress.ThrowIfCancelled(what);
                        job.Errors.Add("Headless Lua " + modeLabel + " run was stopped.");
                        return;
                    }
                    if (clock.ElapsedMilliseconds >= LuaRunTimeoutMs)
                    {
                        StopLuaProcess(process);
                        job.Errors.Add("Headless Lua " + modeLabel + " refresh timed out after " + (LuaRunTimeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + " seconds.");
                        return;
                    }
                }
                process.WaitForExit();

                lock (outputLock)
                {
                    int unclassified = 0;
                    foreach (string raw in outputLines)
                    {
                        string line = raw ?? string.Empty;
                        if (line.StartsWith("JOYPRO_PROFILE_OK|", StringComparison.Ordinal))
                            job.Succeeded++;
                        else if (line.StartsWith("JOYPRO_PROFILE_ERR|", StringComparison.Ordinal))
                        {
                            job.Failed++;
                            job.Warnings.Add("[headless-lua] " + line.Substring("JOYPRO_PROFILE_ERR|".Length));
                        }
                        else if (line.StartsWith("JOYPRO_TEMPLATE|", StringComparison.Ordinal))
                        {
                            string template = line.Substring("JOYPRO_TEMPLATE|".Length);
                            if (!job.Templates.Contains(template))
                                job.Templates.Add(template);
                        }
                        else if (line.StartsWith("JOYPRO_DEVICES|", StringComparison.Ordinal))
                            job.DeviceCount = line.Substring("JOYPRO_DEVICES|".Length);
                        else if (line.StartsWith("JOYPRO_NAMES|", StringComparison.Ordinal))
                            job.NamesCount = line.Substring("JOYPRO_NAMES|".Length);
                        else if (line.StartsWith("JOYPRO_COST|", StringComparison.Ordinal))
                        {
                            string[] parts = line.Split('|');
                            int candidate;
                            double seconds;
                            if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out candidate) &&
                                double.TryParse(parts[2].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                                job.Costs[candidate] = seconds;
                        }
                        else if (line.StartsWith("JOYPRO_FASTCLEAR|", StringComparison.Ordinal))
                            job.FastClear = line.EndsWith("|1", StringComparison.Ordinal);
                        else if (line.StartsWith("JOYPRO_DONE|", StringComparison.Ordinal))
                        {
                            job.SawDone = true;
                            string[] parts = line.Substring("JOYPRO_DONE|".Length).Split('|');
                            int[] summary = new int[3];
                            for (int i = 0; i < 3 && i < parts.Length; i++)
                                int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out summary[i]);
                            job.DoneSummary = summary;
                        }
                        else if (line.StartsWith("JOYPRO_FATAL|", StringComparison.Ordinal))
                            job.Errors.Add("Headless Lua fatal error: " + line.Substring("JOYPRO_FATAL|".Length));
                        else if (!string.IsNullOrWhiteSpace(line) && unclassified < 20)
                        {
                            unclassified++;
                            job.Warnings.Add("[headless-lua] " + line);
                        }
                    }
                    foreach (string err in errorLines)
                    {
                        if (!string.IsNullOrWhiteSpace(err))
                            job.Warnings.Add("[headless-lua stderr] " + err);
                    }
                }

                if (!job.SawDone)
                    job.Warnings.Add("Headless Lua run did not emit completion marker.");
                if (process.ExitCode != 0 && job.Errors.Count == 0)
                    job.Errors.Add("Headless Lua " + modeLabel + " refresh failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                // The run of DCS's default profiles has no aircraft of its own.
                if (job.Succeeded <= 0 && job.Errors.Count == 0 && job.Assigned.Count > 0)
                    job.Errors.Add(exportHtml ? "Headless Lua refresh did not export any profile HTML." : "Headless Lua clean refresh did not clear/save any profile assignments.");
                if (job.Failed > 0)
                    job.Warnings.Add("Headless Lua " + modeLabel + " refresh reported " + job.Failed.ToString(CultureInfo.InvariantCulture) + " profile error(s).");
                job.Ok = job.Errors.Count == 0;
                JoyPro.Logging.JoyProLog.Debug("DcsRefresh", "luae.exe " + modeLabel + " run '" + job.Name + "' (" + job.Assigned.Count.ToString(CultureInfo.InvariantCulture) +
                                                             " aircraft) took " + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, exit code " +
                                                             process.ExitCode.ToString(CultureInfo.InvariantCulture) + (job.Ok ? string.Empty : " (" + (job.SawDone ? "after" : "before") + " its end marker, last step: " + job.LastProgress + ", last line: " + (outputLines.Count > 0 ? outputLines[outputLines.Count - 1] : "") + "): " + string.Join(" ", job.Errors)));
            }
        }

        private static void StopLuaProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // ignore kill exceptions
            }
        }

        // The runs' findings in their order (warnings, device types, counts) as one run reported them before.
        private static void MergeLuaJobs(DcsRefreshResult result, DcsLuaPlan plan, IEnumerable<DcsLuaJob> jobs, bool exportHtml)
        {
            string modeLabel = exportHtml ? "ID" : "clean";
            List<DcsLuaJob> ordered = jobs.OrderBy(j => j.Order).ToList();
            int succeeded = 0;
            int[] summary = new int[3];
            string deviceCount = null;
            string namesCount = null;
            bool fastClear = false;
            foreach (DcsLuaJob job in ordered)
            {
                result.Warnings.AddRange(job.Warnings);
                foreach (string template in job.Templates)
                {
                    if (!result.CoveredDeviceTypes.Contains(template))
                        result.CoveredDeviceTypes.Add(template);
                }
                // A slow aircraft cleared by several runs counts once.
                if (job.FakePart != 0)
                    continue;
                succeeded += job.Succeeded;
                if (job.DoneSummary != null)
                {
                    for (int i = 0; i < 3; i++)
                        summary[i] += job.DoneSummary[i];
                }
                deviceCount = deviceCount ?? job.DeviceCount;
                namesCount = namesCount ?? job.NamesCount;
                fastClear |= job.FastClear;
            }
            result.Steps.Add("Headless Lua " + modeLabel + " refresh: " + ordered.Count.ToString(CultureInfo.InvariantCulture) + " luae.exe run(s), " +
                             Math.Min(plan.Workers, ordered.Count).ToString(CultureInfo.InvariantCulture) + " at once, up to " +
                             plan.ChunkSize.ToString(CultureInfo.InvariantCulture) + " aircraft per run.");
            if (!exportHtml)
            {
                int templates = ordered.Where(j => j.DeviceTemplates != null).SelectMany(j => j.DeviceTemplates).Distinct(StringComparer.Ordinal).Count();
                result.Steps.Add("Device types with their own DCS default files: " + templates.ToString(CultureInfo.InvariantCulture) + ".");
                if (fastClear)
                    result.Steps.Add("DCS's clearProfile validated each command once (after its last device).");
            }
            if (deviceCount != null)
                result.Steps.Add("Headless Lua device count: " + deviceCount);
            if (namesCount != null)
            {
                result.Steps.Add("English DCS command names: " + namesCount + ".");
                if (namesCount == "0")
                    result.Warnings.Add("DCS's English command names (l10n/en/LC_MESSAGES/input.mo) were not found; modules that name their commands by a key keep the key.");
            }
            result.Steps.Add("Headless Lua " + modeLabel + " summary: " + string.Join("|", summary.Select(v => v.ToString(CultureInfo.InvariantCulture))));
            result.PlanesScanned = Math.Max(result.PlanesScanned, succeeded);
            result.ModulesToScanResolved = Math.Max(result.ModulesToScanResolved, succeeded);
            result.Steps.Add(exportHtml
                ? "Headless Lua exported HTML for " + succeeded.ToString(CultureInfo.InvariantCulture) + " profile(s)."
                : "Headless Lua cleared/saved assignments for " + succeeded.ToString(CultureInfo.InvariantCulture) + " profile(s).");
        }

        // Device type names with their own default file per joystick folder, read once per folder (the runs and the
        // clean profile writer ask for the same folders).
        private sealed class DcsTemplateCache
        {
            private readonly Dictionary<string, List<string>> byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> installFolders = new List<string>();
            private readonly List<string> savedGamesTemplates;

            public DcsTemplateCache(string installPath, string savedGamesPath)
            {
                string aircrafts = Path.Combine(installPath ?? string.Empty, "Config", "Input", "Aircrafts");
                try
                {
                    if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(aircrafts))
                        installFolders.AddRange(Directory.GetDirectories(aircrafts).Select(d => Path.Combine(d, "joystick")));
                }
                catch (Exception)
                {
                }
                savedGamesTemplates = CollectSavedGamesDeviceTemplates(savedGamesPath);
            }

            public List<string> Folder(string folder)
            {
                List<string> templates;
                lock (byFolder)
                {
                    if (byFolder.TryGetValue(folder, out templates))
                        return templates;
                }
                templates = CollectDeviceTemplatesOfFolder(folder);
                lock (byFolder)
                    byFolder[folder] = templates;
                return templates;
            }

            // = CollectDeviceTemplates(install, candidates) plus the user's device types (the order DCS gets them in).
            public List<string> ForRun(IEnumerable<DcsHeadlessProfileCandidate> candidates)
            {
                List<string> folders = new List<string>();
                folders.AddRange(candidates.Where(c => c != null && !string.IsNullOrWhiteSpace(c.FolderPath)).Select(c => Path.Combine(c.FolderPath, "joystick")));
                folders.AddRange(installFolders);
                SortedSet<string> templates = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string template in Folder(folder))
                        templates.Add(template);
                }
                List<string> list = templates.ToList();
                foreach (string template in savedGamesTemplates)
                {
                    if (!list.Contains(template))
                        list.Add(template);
                }
                return list;
            }

            // = TemplatesByPlane: per unit, the device types with a default file in its own joystick folder.
            public Dictionary<string, HashSet<string>> ByPlane(List<DcsHeadlessProfileCandidate> candidates)
            {
                Dictionary<string, HashSet<string>> map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (DcsHeadlessProfileCandidate candidate in candidates ?? new List<DcsHeadlessProfileCandidate>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.UnitName) || string.IsNullOrWhiteSpace(candidate.FolderPath))
                        continue;
                    HashSet<string> set;
                    if (!map.TryGetValue(candidate.UnitName, out set))
                        map[candidate.UnitName] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string template in Folder(Path.Combine(candidate.FolderPath, "joystick")))
                        set.Add(template);
                }
                return map;
            }
        }

        // The device type names of one joystick folder (see CollectDeviceTemplates).
        private static List<string> CollectDeviceTemplatesOfFolder(string folder)
        {
            List<string> templates = new List<string>();
            string[] files;
            try
            {
                if (!Directory.Exists(folder))
                    return templates;
                files = Directory.GetFiles(folder, "*.lua");
            }
            catch (Exception)
            {
                return templates;
            }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".diff.lua".Length);
                else
                    name = name.Substring(0, name.Length - ".lua".Length);
                if (name.Length == 0 || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) || name.Trim().Length == 0)
                    continue;
                templates.Add(name);
            }
            return templates;
        }
    }
}
