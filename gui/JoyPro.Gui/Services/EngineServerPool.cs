using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

// The app's engine servers ("engine serve", engine/EngineServer.cs): one long-running engine per engine path and
// environment runs the served commands (EngineServerCommands) one after another instead of a process per call.
// JOYPRO_ENGINE_SERVER=off turns it off (every call in a process of its own, as before).
internal static class EngineServerPool
{
    public const string SwitchVariable = "JOYPRO_ENGINE_SERVER";

    // Variables that belong to one call; every other variable is part of the server's own environment.
    private static readonly HashSet<string> CallVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "JOYPRO_CANCEL_FILE", JoyProLog.ParentVariable, JoyProLog.SessionVariable, "JOYPRO_ARGS_FILE"
    };

    private const int MaxServers = 3;
    private static readonly object Sync = new();
    private static readonly List<EngineServerProcess> Servers = [];
    private static bool exitHooked;

    // Servers started by this app (tests, the log).
    public static int Starts;

    public static bool Enabled => !string.Equals(Environment.GetEnvironmentVariable(SwitchVariable), "off", StringComparison.OrdinalIgnoreCase);

    // The command's result from a server; null when no server could run it (the caller starts a process instead).
    public static async Task<EngineCommandResult?> TryRunAsync(string engineExePath, IReadOnlyList<string> arguments, string callId,
        EngineRunOptions? options, CancellationToken cancellationToken)
    {
        Dictionary<string, string> callEnvironment = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> variable in JoyProLog.ChildEnvironment(callId))
            callEnvironment[variable.Key] = variable.Value;
        Dictionary<string, string> serverEnvironment = new(StringComparer.OrdinalIgnoreCase);
        if (options?.Environment != null)
        {
            foreach (KeyValuePair<string, string> variable in options.Environment)
            {
                if (CallVariables.Contains(variable.Key))
                    callEnvironment[variable.Key] = variable.Value;
                else
                    serverEnvironment[variable.Key] = variable.Value;
            }
        }
        string key = Key(engineExePath, serverEnvironment);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            EngineServerProcess? server = Get(key, engineExePath, serverEnvironment);
            if (server == null)
                return null;
            EngineServerProcess.Outcome outcome = await server.RunAsync(arguments, callEnvironment, options?.StandardErrorLine, cancellationToken).ConfigureAwait(false);
            if (outcome.Result != null)
                return outcome.Result;
            Remove(server);
            // Not sent, or refused before it ran (game data changed): a new server runs it.
            if (!outcome.MayHaveRun)
                continue;
            JoyProLog.Warn("Engine", "The engine server ended during a command (" + JoyProLog.JoinArguments(arguments) + ")");
            return new EngineCommandResult
            {
                ExitCode = 1,
                Duration = outcome.Duration,
                StandardOutput = outcome.StandardOutput,
                StandardError = outcome.StandardError + "engine error: the engine stopped while running the command. Please try again." + Environment.NewLine,
                Arguments = arguments
            };
        }
        return null;
    }

    // Starts the server for this engine (and the current environment) before the first call needs it: the server reads
    // the game lists while the app starts (engine EngineServer.WarmUp). The task ends when it is ready.
    public static async Task PrewarmAsync(string engineExePath, EngineRunOptions? options = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(engineExePath) || !File.Exists(engineExePath))
            return;
        try
        {
            EngineCommandResult? result = await TryRunAsync(engineExePath, ["--status", "false", "--benchmark", "false", "version"], JoyProLog.NewCallId(), options, CancellationToken.None).ConfigureAwait(false);
            JoyProLog.Info("Engine", "Engine server ready" + (result == null ? " (not started: calls run in processes of their own)" : string.Empty));
        }
        catch (Exception ex)
        {
            JoyProLog.Warn("Engine", "The engine server could not be started ahead", ex);
        }
    }

    // A command in a process of its own changed (or may have changed) game data: the next served call gets a new server.
    public static void Recycle(string reason)
    {
        List<EngineServerProcess> old;
        lock (Sync)
        {
            if (Servers.Count == 0)
                return;
            old = Servers.ToList();
            Servers.Clear();
        }
        JoyProLog.Info("Engine", "Engine servers restart for the next call: " + reason);
        foreach (EngineServerProcess server in old)
            server.Dispose();
    }

    public static void StopAll()
    {
        List<EngineServerProcess> old;
        lock (Sync)
        {
            old = Servers.ToList();
            Servers.Clear();
        }
        foreach (EngineServerProcess server in old)
            server.Dispose(immediately: true);
    }

    private static string Key(string engineExePath, Dictionary<string, string> serverEnvironment)
    {
        StringBuilder sb = new(Path.GetFullPath(engineExePath).ToLowerInvariant());
        SortedDictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key?.ToString() ?? string.Empty;
            if (name.StartsWith("JOYPRO_", StringComparison.OrdinalIgnoreCase) && !CallVariables.Contains(name))
                variables[name] = entry.Value?.ToString() ?? string.Empty;
        }
        foreach (KeyValuePair<string, string> variable in serverEnvironment)
            variables[variable.Key] = variable.Value;
        foreach (KeyValuePair<string, string> variable in variables)
            sb.Append('\n').Append(variable.Key.ToUpperInvariant()).Append('=').Append(variable.Value);
        return sb.ToString();
    }

    private static EngineServerProcess? Get(string key, string engineExePath, Dictionary<string, string> serverEnvironment)
    {
        EngineServerProcess? stale = null;
        EngineServerProcess? server;
        lock (Sync)
        {
            if (!exitHooked)
            {
                exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();
            }
            server = Servers.FirstOrDefault(s => s.Key == key && !s.HasEnded);
            if (server != null)
            {
                Servers.Remove(server);
                Servers.Insert(0, server);
                return server;
            }
            Servers.RemoveAll(s => s.HasEnded);
            server = EngineServerProcess.Start(key, engineExePath, serverEnvironment);
            Starts++;
            if (server == null)
                return null;
            Servers.Insert(0, server);
            if (Servers.Count > MaxServers)
            {
                stale = Servers[^1];
                Servers.RemoveAt(Servers.Count - 1);
            }
        }
        stale?.Dispose();
        return server;
    }

    private static void Remove(EngineServerProcess server)
    {
        lock (Sync)
            Servers.Remove(server);
        server.Dispose();
    }
}

// One "engine serve" process and its frames (engine/EngineServer.cs has the protocol).
internal sealed class EngineServerProcess : IDisposable
{
    internal sealed class Outcome
    {
        public EngineCommandResult? Result;
        public bool MayHaveRun;
        public string StandardOutput = string.Empty;
        public string StandardError = string.Empty;
        public TimeSpan Duration;
    }

    private static readonly UTF8Encoding Utf8 = new(false);
    // Every server process this app started (tests check that none outlives its use).
    internal static readonly List<int> StartedProcessIds = [];
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    private EngineServerProcess(string key, Process process)
    {
        Key = key;
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
    }

    public string Key { get; }

    public bool HasEnded
    {
        get
        {
            try
            {
                return Volatile.Read(ref disposed) != 0 || process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static EngineServerProcess? Start(string key, string engineExePath, Dictionary<string, string> serverEnvironment)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = engineExePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Frames go to the raw stream; the stdin writer must not put a byte order mark in front of them.
                StandardInputEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("serve");
            string? engineDirectory = Path.GetDirectoryName(engineExePath);
            if (!string.IsNullOrWhiteSpace(engineDirectory))
                startInfo.WorkingDirectory = engineDirectory;
            startInfo.Environment["JOYPRO_CALLER"] = "gui";
            startInfo.Environment["JOYPRO_STDIO"] = "utf8";
            startInfo.Environment["JOYPRO_SERVER_PARENT"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (KeyValuePair<string, string> variable in JoyProLog.ChildEnvironment(string.Empty))
                startInfo.Environment[variable.Key] = variable.Value;
            foreach (KeyValuePair<string, string> variable in serverEnvironment)
                startInfo.Environment[variable.Key] = variable.Value;
            Process process = new() { StartInfo = startInfo };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    JoyProLog.Warn("Engine", "Engine server: " + e.Data);
            };
            process.Start();
            lock (StartedProcessIds)
                StartedProcessIds.Add(process.Id);
            process.BeginErrorReadLine();
            EngineServerProcess server = new(key, process);
            server.ready = server.WaitForHelloAsync(engineExePath);
            return server;
        }
        catch (Exception ex)
        {
            JoyProLog.Warn("Engine", "The engine server could not be started (" + engineExePath + ")", ex);
            return null;
        }
    }

    private Task<bool> ready = Task.FromResult(false);

    // The hello frame: the server is up (an engine that answers nothing within 30 s is not used).
    private async Task<bool> WaitForHelloAsync(string engineExePath)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Task<(char Kind, byte[] Payload)?> hello = ReadFrameAsync();
        Task first = await Task.WhenAny(hello, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (first != hello || hello.Result is not { Kind: 'H' })
        {
            JoyProLog.Warn("Engine", "The engine server did not start (" + engineExePath + "): the call runs in a process of its own");
            Kill();
            return false;
        }
        JoyProLog.Info("Engine", "Engine server started (pid " + process.Id.ToString(CultureInfo.InvariantCulture) + ", " +
                                 watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
        return true;
    }

    public async Task<Outcome> RunAsync(IReadOnlyList<string> arguments, Dictionary<string, string> environment, Action<string>? onErrorLine,
        CancellationToken cancellationToken)
    {
        Outcome outcome = new();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch watch = Stopwatch.StartNew();
        StringBuilder stderr = new();
        string stdout = string.Empty;
        try
        {
            if (!await ready.ConfigureAwait(false) || HasEnded)
                return outcome;
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["Args"] = arguments.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
                ["Env"] = environment
            });
            try
            {
                await WriteFrameAsync('R', request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return outcome;
            }
            outcome.MayHaveRun = true;
            bool receivedAny = false;
            using CancellationTokenRegistration registration = cancellationToken.Register(Kill);
            while (true)
            {
                (char Kind, byte[] Payload)? frame = await ReadFrameAsync().ConfigureAwait(false);
                if (frame == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Ended cleanly without a word about this command (its idle time ran out as the command came): the
                    // command did not run, a new server runs it.
                    if (!receivedAny && ExitedCleanly())
                        outcome.MayHaveRun = false;
                    outcome.StandardOutput = stdout;
                    outcome.StandardError = stderr.ToString();
                    outcome.Duration = watch.Elapsed;
                    return outcome;
                }
                receivedAny |= frame.Value.Kind is 'E' or 'O' or 'X';
                switch (frame.Value.Kind)
                {
                    case 'S':
                        // Refused before it ran: game data changed since the server read it.
                        outcome.MayHaveRun = false;
                        return outcome;
                    case 'E':
                        {
                            string line = Utf8.GetString(frame.Value.Payload);
                            stderr.Append(line).Append(Environment.NewLine);
                            if (onErrorLine != null)
                            {
                                try
                                {
                                    onErrorLine(line);
                                }
                                catch (Exception ex)
                                {
                                    JoyProLog.Warn("Engine", "The live stderr handler failed", ex);
                                }
                            }
                            break;
                        }
                    case 'O':
                        stdout = Utf8.GetString(frame.Value.Payload);
                        break;
                    case 'X':
                        outcome.Result = new EngineCommandResult
                        {
                            ExitCode = int.Parse(Utf8.GetString(frame.Value.Payload), NumberStyles.Integer, CultureInfo.InvariantCulture),
                            Duration = watch.Elapsed,
                            StandardOutput = stdout,
                            StandardError = stderr.ToString(),
                            Arguments = arguments
                        };
                        return outcome;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteFrameAsync(char kind, byte[] payload)
    {
        byte[] header = new byte[5];
        header[0] = (byte)kind;
        BitConverter.GetBytes(payload.Length).CopyTo(header, 1);
        await input.WriteAsync(header).ConfigureAwait(false);
        await input.WriteAsync(payload).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    private async Task<(char Kind, byte[] Payload)?> ReadFrameAsync()
    {
        try
        {
            byte[] header = new byte[5];
            if (!await ReadExactlyAsync(header).ConfigureAwait(false))
                return null;
            int length = BitConverter.ToInt32(header, 1);
            if (length < 0 || length > 512 * 1024 * 1024)
                return null;
            byte[] payload = new byte[length];
            if (!await ReadExactlyAsync(payload).ConfigureAwait(false))
                return null;
            return ((char)header[0], payload);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await output.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    private bool ExitedCleanly()
    {
        try
        {
            return process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => Dispose(immediately: false);

    // Quits once the command it runs (if any) is done: 'Q', closed stdin, a kill 5 s later at the latest. immediately:
    // the app is ending, no waiting.
    public void Dispose(bool immediately)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (immediately)
        {
            Kill();
            return;
        }
        Task.Run(async () =>
        {
            bool entered = await gate.WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                {
                    byte[] quit = [(byte)'Q', 0, 0, 0, 0];
                    input.Write(quit, 0, quit.Length);
                    input.Flush();
                    input.Close();
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
            finally
            {
                if (entered)
                    gate.Release();
                process.Dispose();
            }
        });
    }
}
