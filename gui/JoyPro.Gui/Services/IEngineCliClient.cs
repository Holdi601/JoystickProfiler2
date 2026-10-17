using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services;

public interface IEngineCliClient
{
    Task<EngineCommandResult> RunAsync(
        string engineExePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    // Like RunAsync, but hands every stderr line to options.StandardErrorLine while the engine runs (the busy area's
    // live progress) and starts it with options.Environment. The result is the same as RunAsync's. The default (for
    // clients that cannot stream) replays the stderr lines once the run ended.
    async Task<EngineCommandResult> RunStreamingAsync(
        string engineExePath,
        IReadOnlyList<string> arguments,
        EngineRunOptions options,
        CancellationToken cancellationToken = default)
    {
        EngineCommandResult result = await RunAsync(engineExePath, arguments, cancellationToken);
        if (options?.StandardErrorLine != null && !string.IsNullOrEmpty(result.StandardError))
        {
            foreach (string line in result.StandardError.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    options.StandardErrorLine(trimmed);
            }
        }
        return result;
    }
}

public sealed class EngineRunOptions
{
    // Called for every stderr line as it arrives, on a background thread.
    public Action<string>? StandardErrorLine { get; init; }

    // Extra environment variables for the engine process (JOYPRO_CANCEL_FILE).
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
