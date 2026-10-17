using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Ffb.Core.Host;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

// One running JoyPro.Ffb.exe (run --protocol): stdout lines in, commands out. Events come on any thread.
public interface IFfbHostProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    event Action<string>? LineReceived;

    event Action<string>? ErrorLineReceived;

    event Action? Exited;

    // One command line ("watch on", "reload", "stop"); false when stdin is already closed.
    bool Send(string command);

    bool WaitForExit(int milliseconds);

    void Kill();
}

public sealed record FfbToolResult(int ExitCode, string StandardOutput, string StandardError);

// Starts JoyPro.Ffb.exe: the long-running "run" process and short tool calls ("devices --json").
public interface IFfbHostLauncher
{
    IFfbHostProcess Start(string exePath, IReadOnlyList<string> arguments);

    Task<FfbToolResult> RunToolAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

// The telemetry force feedback page's ties to the PC, replaceable like ExternalProcess.Start: the tests swap in fakes,
// so no test ever starts JoyPro.Ffb, looks at the real game processes or writes into the user's data folder.
public static class FfbTelemetryServices
{
    public static IFfbHostLauncher Launcher { get; set; } = new FfbHostLauncher();

    // The pid of the first running process with one of the names (without ".exe"), or null.
    public static Func<IReadOnlyList<string>, int?> FindGameProcess { get; set; } = GameProcessLookup.FindFirstPid;

    public static Func<string> GraphFolder { get; set; } = TelemetryFfbPaths.GraphFolder;

    public static Func<string> ConfigFile { get; set; } = TelemetryFfbPaths.ConfigFile;

    // <GUI folder>\ffb\JoyPro.Ffb.exe (bundled by the BuildAndBundleFfbProcess target).
    public static Func<string> ExePath { get; set; } = () => Path.Combine(AppContext.BaseDirectory, "ffb", "JoyPro.Ffb.exe");
}

// Whether a game runs, by process name. Process.GetProcessesByName walks the whole process table (a snapshot of every
// process on the PC, a few milliseconds); the callers do it every few seconds at most, and every Process object is
// disposed right away (each one holds a handle otherwise until the finalizer runs).
public static class GameProcessLookup
{
    public static bool IsRunning(string processName) => FindPid(processName) != null;

    public static int? FindPid(string processName)
    {
        Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
        int? pid = null;
        foreach (Process process in processes)
        {
            pid ??= process.Id;
            process.Dispose();
        }
        return pid;
    }

    public static int? FindFirstPid(IReadOnlyList<string> processNames)
    {
        foreach (string name in processNames)
        {
            if (FindPid(name) is int pid)
                return pid;
        }
        return null;
    }
}

public sealed class FfbHostLauncher : IFfbHostLauncher
{
    public IFfbHostProcess Start(string exePath, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(exePath))
        {
            JoyProLog.Warn("Ffb", "JoyPro.Ffb.exe is missing: " + exePath);
            throw new FileNotFoundException("JoyPro.Ffb.exe is missing (expected next to the GUI in ffb\\).", exePath);
        }
        string callId = JoyProLog.NewCallId();
        Process process = new() { StartInfo = CreateStartInfo(exePath, arguments, redirectInput: true, callId), EnableRaisingEvents = true };
        JoyProLog.Info("Ffb", "Call " + callId + ": JoyPro.Ffb " + JoyProLog.JoinArguments(arguments));
        FfbHostProcess host = new(process);
        JoyProLog.Debug("Ffb", "Call " + callId + ": JoyPro.Ffb runs as pid " + host.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.Exited += (_, _) =>
        {
            int code = -1;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
            }
            JoyProLog.Info("Ffb", "Call " + callId + ": JoyPro.Ffb exited with code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        return host;
    }

    public async Task<FfbToolResult> RunToolAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(exePath))
            return new FfbToolResult(-1, string.Empty, "JoyPro.Ffb.exe is missing: " + exePath);
        string callId = JoyProLog.NewCallId();
        using Process process = new() { StartInfo = CreateStartInfo(exePath, arguments, redirectInput: false, callId) };
        JoyProLog.Info("Ffb", "Call " + callId + ": JoyPro.Ffb " + JoyProLog.JoinArguments(arguments));
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        FfbToolResult result = new(process.ExitCode, await output, await error);
        JoyProLog.Write(result.ExitCode == 0 ? JoyProLogLevel.Info : JoyProLogLevel.Warn, "Ffb",
            "Call " + callId + " finished: exit code " + result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            (result.StandardError.Trim().Length > 0 ? ", stderr:" + Environment.NewLine + JoyProLog.Truncate(result.StandardError.TrimEnd(), 8192) : string.Empty), null);
        return result;
    }

    private static ProcessStartInfo CreateStartInfo(string exePath, IReadOnlyList<string> arguments, bool redirectInput, string callId)
    {
        ProcessStartInfo info = new(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (KeyValuePair<string, string> variable in JoyProLog.ChildEnvironment(callId))
            info.Environment[variable.Key] = variable.Value;
        return info;
    }

    private sealed class FfbHostProcess : IFfbHostProcess
    {
        private readonly Process process;
        private readonly object inputGate = new();
        private bool inputClosed;

        public FfbHostProcess(Process process)
        {
            this.process = process;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    LineReceived?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    ErrorLineReceived?.Invoke(e.Data);
            };
            process.Exited += (_, _) => Exited?.Invoke();
            process.Start();
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Id = process.Id;
        }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public event Action<string>? LineReceived;

        public event Action<string>? ErrorLineReceived;

        public event Action? Exited;

        public bool Send(string command)
        {
            lock (inputGate)
            {
                if (inputClosed || HasExited)
                    return false;
                try
                {
                    process.StandardInput.WriteLine(command);
                    return true;
                }
                catch (IOException)
                {
                    inputClosed = true;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    inputClosed = true;
                    return false;
                }
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            try
            {
                return process.WaitForExit(milliseconds);
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            lock (inputGate)
            {
                if (!inputClosed)
                {
                    inputClosed = true;
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            process.Dispose();
        }
    }
}
