using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Watch;

// What the tray host needs from the game config watcher (GameConfigWatcher; a fake in the tests).
public interface IGameWatcher : IDisposable
{
    // The watch key, raised on a thread-pool thread once the config changed and the game is closed.
    event Action<string>? ConfigChanged;

    void Update(IEnumerable<GameWatchSpec> specs);

    void Trigger(string gameId);
}

public sealed record EngineRun(int ExitCode, string StandardOutput, string StandardError);

// One engine call (the tray host's; the GUI has its own client with its options and output pane).
public interface IWatchEngine
{
    Task<EngineRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

// engine.exe as a hidden process: closed stdin (never a prompt), output read in the console's code page.
public sealed class ProcessWatchEngine : IWatchEngine
{
    private static readonly Encoding OutputEncoding = ResolveOutputEncoding();

    public ProcessWatchEngine(string engineExePath)
    {
        EnginePath = engineExePath;
    }

    public string EnginePath { get; }

    public async Task<EngineRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(EnginePath))
            return new EngineRun(-1, string.Empty, "The engine was not found: " + EnginePath);
        ProcessStartInfo startInfo = new()
        {
            FileName = EnginePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Crash reports of engines the app started go where the app shows them.
            Environment = { ["JOYPRO_CALLER"] = "gui" },
            WorkingDirectory = Path.GetDirectoryName(EnginePath) ?? string.Empty
        };
        foreach (string argument in arguments)
        {
            if (!string.IsNullOrWhiteSpace(argument))
                startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new EngineRun(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new EngineRun(-1, string.Empty, ex.Message);
        }
    }

    private static Encoding ResolveOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (codePage > 0)
                return Encoding.GetEncoding(codePage);
        }
        catch (Exception)
        {
            // Fall back below.
        }
        return Encoding.UTF8;
    }
}
