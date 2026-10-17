using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Gui.Services;

public enum EngineStderrKind
{
    Other,
    Status,
    Progress,
    Cancel
}

// One stderr line of the engine as the busy area reads it (engine/EngineProgress.cs, ENGINE_CLI_WIKI "--progress"):
//   [engine-status] <message>
//   [engine-progress] <current>/<total> <phase>: <item>     (total "?" when unknown)
//   [engine-cancel] available | unavailable | cancelled
// Everything else (warnings, errors) is Other.
public sealed record EngineStderrLine(
    EngineStderrKind Kind,
    string Text,
    int Current = 0,
    int? Total = null,
    string Phase = "",
    string Item = "",
    string CancelState = "");

public static class EngineProgressParser
{
    public const string StatusPrefix = "[engine-status] ";
    public const string ProgressPrefix = "[engine-progress] ";
    public const string CancelPrefix = "[engine-cancel] ";

    private static readonly Regex ProgressLine = new(@"^(?<current>\d+)/(?<total>\d+|\?) (?<phase>[^:]+?)(?:: (?<item>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static EngineStderrLine Parse(string? line)
    {
        string text = (line ?? string.Empty).TrimEnd('\r', '\n');
        if (text.StartsWith(StatusPrefix, StringComparison.Ordinal))
            return new EngineStderrLine(EngineStderrKind.Status, text.Substring(StatusPrefix.Length).Trim());
        if (text.StartsWith(CancelPrefix, StringComparison.Ordinal))
        {
            string state = text.Substring(CancelPrefix.Length).Trim().ToLowerInvariant();
            return new EngineStderrLine(EngineStderrKind.Cancel, text, CancelState: state);
        }
        if (text.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            Match match = ProgressLine.Match(text.Substring(ProgressPrefix.Length).Trim());
            if (match.Success &&
                int.TryParse(match.Groups["current"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int current))
            {
                int? total = int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTotal) && parsedTotal > 0
                    ? parsedTotal
                    : null;
                return new EngineStderrLine(EngineStderrKind.Progress, text, current, total,
                    match.Groups["phase"].Value.Trim(), match.Groups["item"].Success ? match.Groups["item"].Value.Trim() : string.Empty);
            }
            return new EngineStderrLine(EngineStderrKind.Other, text);
        }
        return new EngineStderrLine(EngineStderrKind.Other, text);
    }

    // stderr for the session log (engine-output.log): every line but the progress, of which each step keeps its first and
    // last item and about twenty in between (a DCS import reports thousands of files).
    public static string ThinForLog(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return string.Empty;
        StringBuilder sb = new();
        string phase = string.Empty;
        int lastKept = 0;
        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            EngineStderrLine parsed = Parse(line);
            if (parsed.Kind == EngineStderrKind.Progress)
            {
                bool newPhase = !string.Equals(parsed.Phase, phase, StringComparison.Ordinal);
                int step = Math.Max(1, (parsed.Total ?? 400) / 20);
                bool keep = newPhase || parsed.Current <= 1 || parsed.Current == parsed.Total || parsed.Current - lastKept >= step;
                phase = parsed.Phase;
                if (!keep)
                    continue;
                lastKept = parsed.Current;
            }
            if (line.Length > 0)
                sb.Append(line).Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    // stderr without the busy area's lines: progress and cancel lines always, status lines unless the user asked for
    // them ("Status Updates" in the engine options). What remains is what the engine warned or failed with.
    public static string StripProgress(string? stderr, bool keepStatus)
    {
        if (string.IsNullOrEmpty(stderr))
            return string.Empty;
        StringBuilder sb = new();
        string[] lines = stderr.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            // The empty piece after the last newline is no line.
            if (i == lines.Length - 1 && line.Length == 0)
                break;
            if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal) || line.StartsWith(CancelPrefix, StringComparison.Ordinal))
                continue;
            if (!keepStatus && line.StartsWith(StatusPrefix, StringComparison.Ordinal))
                continue;
            sb.Append(line).Append(Environment.NewLine);
        }
        string result = sb.ToString();
        // Keep the original shape: an input without a final newline gets none either.
        if (!stderr.EndsWith('\n') && result.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            result = result.Substring(0, result.Length - Environment.NewLine.Length);
        return result;
    }
}
