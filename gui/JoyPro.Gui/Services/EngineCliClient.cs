using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

public sealed class EngineCliClient : IEngineCliClient
{
    private static readonly Encoding ProcessOutputEncoding = ResolveProcessOutputEncoding();

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

        string? engineDirectory = System.IO.Path.GetDirectoryName(engineExePath);
        if (!string.IsNullOrWhiteSpace(engineDirectory))
            startInfo.WorkingDirectory = engineDirectory;

        if (arguments != null)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                string arg = arguments[i];
                if (!string.IsNullOrWhiteSpace(arg))
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

    private static Encoding ResolveProcessOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Ignore when provider is already registered or unavailable.
        }

        try
        {
            int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (codePage > 0)
                return Encoding.GetEncoding(codePage);
        }
        catch
        {
            // Fall back below.
        }

        return Encoding.Latin1;
    }
}
