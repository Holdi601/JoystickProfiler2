using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

public sealed class EngineCliClient : IEngineCliClient
{
    // The engine writes UTF-8 when asked (JOYPRO_STDIO=utf8, engine EngineLogging.UseUtf8ConsoleIfAsked): the console's
    // code page cannot hold every title, and its best-fit mapping broke the JSON of "relations suggest".
    private static readonly Encoding ProcessOutputEncoding = new UTF8Encoding(false);

    // A Windows command line holds at most 32767 characters; a call with hundreds of actions (each DCS one with its
    // aircraft) can be longer. Above this length the arguments go in a file the engine reads (engine ExpandArgsFile).
    internal const int ArgsFileThreshold = 16000;

    public Task<EngineCommandResult> RunAsync(
        string engineExePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        return RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);
    }

    public async Task<EngineCommandResult> RunStreamingAsync(
        string engineExePath,
        IReadOnlyList<string> arguments,
        EngineRunOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(engineExePath))
            throw new ArgumentException("Engine path is required.", nameof(engineExePath));

        // The quick interactive commands run in the app's engine server (EngineServerPool, EngineServerCommands): no
        // process start, no new read of the profile and the game lists per call.
        IReadOnlyList<string> requested = arguments ?? Array.Empty<string>();
        bool serverEnabled = EngineServerPool.Enabled;
        if (serverEnabled && EngineServerCommands.IsServed(requested))
        {
            string serverCallId = JoyProLog.NewCallId();
            JoyProLog.Info("Engine", "Call " + serverCallId + " (engine server): engine " + JoyProLog.JoinArguments(requested));
            EngineCommandResult? served = await EngineServerPool.TryRunAsync(engineExePath, requested, serverCallId, options, cancellationToken);
            if (served != null)
            {
                string servedErrors = EngineProgressParser.StripProgress(served.StandardError, keepStatus: true);
                JoyProLog.Write(served.ExitCode == 0 ? JoyProLogLevel.Info : JoyProLogLevel.Warn, "Engine",
                    "Call " + serverCallId + " (engine server) finished: exit code " + served.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " after " + ((long)served.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms, stdout " + served.StandardOutput.Length.ToString(CultureInfo.InvariantCulture) + " chars" +
                    (servedErrors.Trim().Length > 0 ? ", stderr:" + Environment.NewLine + JoyProLog.Truncate(servedErrors.TrimEnd(), 8192) : string.Empty), null);
                return served;
            }
        }
        try
        {
            return await RunProcessAsync(engineExePath, arguments, options, cancellationToken);
        }
        finally
        {
            // A command of its own process may have changed game data the server keeps: a new server next time.
            if (serverEnabled && !EngineServerCommands.KeepsServer(requested))
                EngineServerPool.Recycle("after engine " + JoyProLog.JoinArguments(requested.Take(6).ToList()));
        }
    }

    internal async Task<EngineCommandResult> RunProcessAsync(
        string engineExePath,
        IReadOnlyList<string>? arguments,
        EngineRunOptions? options,
        CancellationToken cancellationToken)
    {

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = engineExePath,
            // The GUI can never answer console prompts. A closed, redirected stdin makes the engine take its
            // non-interactive paths instead of blocking or reading an empty answer.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ProcessOutputEncoding,
            StandardErrorEncoding = ProcessOutputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Crash reports of engines the app started go where the app shows them (engine Program.WriteCrashReport).
        startInfo.Environment["JOYPRO_CALLER"] = "gui";
        startInfo.Environment["JOYPRO_STDIO"] = "utf8";

        string? engineDirectory = System.IO.Path.GetDirectoryName(engineExePath);
        if (!string.IsNullOrWhiteSpace(engineDirectory))
            startInfo.WorkingDirectory = engineDirectory;

        string? argsFile = null;
        if (arguments != null)
        {
            List<string> passed = [];
            for (int i = 0; i < arguments.Count; i++)
            {
                string arg = arguments[i];
                if (!string.IsNullOrWhiteSpace(arg))
                    passed.Add(arg);
            }
            if (CommandLineLength(passed) > ArgsFileThreshold)
            {
                argsFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "joypro-args-" + Guid.NewGuid().ToString("N") + ".json");
                System.IO.File.WriteAllText(argsFile, System.Text.Json.JsonSerializer.Serialize(passed), ProcessOutputEncoding);
                startInfo.Environment["JOYPRO_ARGS_FILE"] = argsFile;
            }
            else
            {
                foreach (string arg in passed)
                    startInfo.ArgumentList.Add(arg);
            }
        }

        // Log correlation (docs/logging.md): the engine's log lines carry this call's id ("c:gui-17").
        string callId = JoyProLog.NewCallId();
        foreach (KeyValuePair<string, string> variable in JoyProLog.ChildEnvironment(callId))
            startInfo.Environment[variable.Key] = variable.Value;
        if (options?.Environment != null)
        {
            foreach (KeyValuePair<string, string> variable in options.Environment)
                startInfo.Environment[variable.Key] = variable.Value;
        }
        Action<string>? onErrorLine = options?.StandardErrorLine;
        JoyProLog.Info("Engine", "Call " + callId + ": engine " + JoyProLog.JoinArguments(arguments ?? Array.Empty<string>()));

        StringBuilder stdout = new StringBuilder();
        StringBuilder stderr = new StringBuilder();

        using ArgsFileCleanup cleanup = new(argsFile);
        using Process process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, evt) =>
        {
            if (evt.Data != null)
                stdout.AppendLine(evt.Data);
        };
        process.ErrorDataReceived += (_, evt) =>
        {
            if (evt.Data == null)
                return;
            lock (stderr)
                stderr.AppendLine(evt.Data);
            if (onErrorLine == null)
                return;
            try
            {
                onErrorLine(evt.Data);
            }
            catch (Exception ex)
            {
                // The live view never breaks the call; the line is in the result anyway.
                JoyProLog.Warn("Engine", "Call " + callId + ": the live stderr handler failed", ex);
            }
        };

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            JoyProLog.Error("Engine", "Call " + callId + ": the engine did not start (" + engineExePath + ")", ex);
            throw;
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            JoyProLog.Warn("Engine", "Call " + callId + " cancelled after " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            throw;
        }
        sw.Stop();

        string errorText;
        lock (stderr)
            errorText = stderr.ToString();
        // The engine logs its progress lines itself (category Progress); here they would only crowd out the warnings.
        string loggedErrors = EngineProgressParser.StripProgress(errorText, keepStatus: true);
        JoyProLog.Write(process.ExitCode == 0 ? JoyProLogLevel.Info : JoyProLogLevel.Warn, "Engine",
            "Call " + callId + " (pid " + process.Id.ToString(CultureInfo.InvariantCulture) + ") finished: exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) +
            " after " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, stdout " + stdout.Length.ToString(CultureInfo.InvariantCulture) + " chars" +
            (loggedErrors.Trim().Length > 0 ? ", stderr:" + Environment.NewLine + JoyProLog.Truncate(loggedErrors.TrimEnd(), 8192) : string.Empty), null);

        return new EngineCommandResult
        {
            ExitCode = process.ExitCode,
            Duration = sw.Elapsed,
            StandardOutput = stdout.ToString(),
            StandardError = errorText,
            Arguments = arguments ?? Array.Empty<string>()
        };
    }

    // The length Windows sees: each argument quoted (with escaped quotes) and a space between them.
    internal static int CommandLineLength(IReadOnlyList<string> arguments)
    {
        int length = 0;
        foreach (string arg in arguments)
            length += arg.Length + 3 + CountQuotes(arg);
        return length;
    }

    private static int CountQuotes(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == '"' || c == '\\')
                count++;
        }
        return count;
    }

    // Deletes the argument file once the engine has finished (or failed to start).
    private sealed class ArgsFileCleanup(string? path) : IDisposable
    {
        public void Dispose()
        {
            if (path == null)
                return;
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                JoyProLog.Warn("Engine", "Could not delete the argument file " + path, ex);
            }
        }
    }
}
