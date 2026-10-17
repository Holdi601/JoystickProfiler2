using System;
using System.Collections.Generic;

namespace JoyPro.Gui.Models;

public sealed class EngineCommandResult
{
    public int ExitCode { get; init; }

    public TimeSpan Duration { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}
