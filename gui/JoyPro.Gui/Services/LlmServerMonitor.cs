using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace JoyPro.Gui.Services;

public sealed record LlmServerSnapshot(bool Running, int Pid, string Model, long MemoryBytes, bool MemoryIsVram, bool Busy, double IdleSecondsLeft)
{
    public static readonly LlmServerSnapshot Stopped = new(false, 0, string.Empty, 0, false, false, 0);
}

// The warm local AI model (engine EngineLlmServer.cs): the engine leaves llama-server loaded between requests and
// describes it in <data>\Llm\server.json; the app owns it. Poll() reports it, puts it into the app's kill-on-close job
// (it dies with the app, even on a crash) and stops it after IdleMinutes without a request; StopNow() ends it at once.
public sealed class LlmServerMonitor
{
    public const string OwnerPidVariable = "JOYPRO_LLM_OWNER_PID";
    public const string IdleMinutesVariable = "JOYPRO_LLM_IDLE_MINUTES";

    private readonly object gate = new();
    private int jobbedPid;
    private IntPtr job = IntPtr.Zero;
    private DateTime vramCheckedUtc = DateTime.MinValue;
    private int vramPid;
    private long? vramBytes;
    private bool vramToolMissing;

    public LlmServerMonitor(string stateFile)
    {
        StateFile = stateFile;
    }

    public string StateFile { get; }

    public double IdleMinutes { get; set; } = 2;

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    // Video memory of a process in bytes (nvidia-smi), null when unknown; tests replace it.
    public Func<int, long?> VramQuery { get; set; } = QueryNvidiaSmi;

    public static string DefaultStateFile()
    {
        string? root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
        return Path.Combine(root, "Llm", "server.json");
    }

    // Engines this app starts may keep the model loaded for the next request (the app stops it when idle).
    public static void PublishOwner(double idleMinutes)
    {
        Environment.SetEnvironmentVariable(OwnerPidVariable, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(IdleMinutesVariable, idleMinutes.ToString(CultureInfo.InvariantCulture));
    }

    public LlmServerSnapshot Poll()
    {
        lock (gate)
        {
            State? state = Read();
            if (state == null)
                return LlmServerSnapshot.Stopped;
            using Process? process = Open(state);
            if (process == null)
            {
                DeleteState();
                return LlmServerSnapshot.Stopped;
            }
            KeepWithApp(process);
            bool busy = IsAlive(state.BusyPid);
            double idleLeft = IdleMinutes * 60;
            if (!busy && DateTime.TryParse(state.LastUsedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime lastUsed))
                idleLeft = IdleMinutes * 60 - (UtcNow() - lastUsed.ToUniversalTime()).TotalSeconds;
            if (!busy && idleLeft <= 0)
            {
                Kill(process);
                DeleteState();
                return LlmServerSnapshot.Stopped;
            }
            long? vram = Vram(state.Pid);
            long memory = vram ?? SafeWorkingSet(process);
            return new LlmServerSnapshot(true, state.Pid, state.Model, memory, vram.HasValue, busy, Math.Max(0, idleLeft));
        }
    }

    // Ends JoyPro's model server now; true when one was running.
    public bool StopNow()
    {
        lock (gate)
        {
            State? state = Read();
            bool stopped = false;
            using (Process? process = Open(state))
            {
                if (process != null)
                    stopped = Kill(process);
            }
            if (state != null)
                DeleteState();
            return stopped;
        }
    }

    private sealed class State
    {
        public int Pid { get; set; }
        public string Model { get; set; } = string.Empty;
        public string StartedUtc { get; set; } = string.Empty;
        public string LastUsedUtc { get; set; } = string.Empty;
        public int BusyPid { get; set; }
    }

    private State? Read()
    {
        try
        {
            if (!File.Exists(StateFile))
                return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void DeleteState()
    {
        try
        {
            File.Delete(StateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The recorded process if it is still the one the engine started (a reused pid is left alone).
    private static Process? Open(State? state)
    {
        if (state == null || state.Pid <= 0)
            return null;
        try
        {
            Process process = Process.GetProcessById(state.Pid);
            if (!process.HasExited && DateTime.TryParse(state.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime started) &&
                Math.Abs((process.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds) < 30)
                return process;
            process.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return null;
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Kill(Process process)
    {
        try
        {
            process.Kill();
            process.WaitForExit(10000);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private long? Vram(int pid)
    {
        if (vramToolMissing && VramQuery == QueryNvidiaSmi)
            return null;
        DateTime now = DateTime.UtcNow;
        if (pid == vramPid && (now - vramCheckedUtc).TotalSeconds < 10)
            return vramBytes;
        vramPid = pid;
        vramCheckedUtc = now;
        try
        {
            vramBytes = VramQuery(pid);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            vramToolMissing = true;
            vramBytes = null;
        }
        return vramBytes;
    }

    // nvidia-smi lists the video memory per process (MiB); other cards: unknown (the working set is shown instead).
    public static long? QueryNvidiaSmi(int pid)
    {
        ProcessStartInfo start = new("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int linePid) && linePid == pid &&
                long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib))
                return mib * 1024 * 1024;
        }
        return null;
    }

    // Kill-on-close job of this app: the model server ends with JoyPro even when JoyPro is killed.
    private void KeepWithApp(Process process)
    {
        if (!OperatingSystem.IsWindows() || process.Id == jobbedPid)
            return;
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    return;
                JobExtendedLimit info = new();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobExtendedLimit>());
            }
            if (AssignProcessToJobObject(job, process.Handle))
                jobbedPid = process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
