using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace JoyPro.Watch;

// One config of engine sync status that is watched: an installed, tracked, enabled game.
public sealed record SyncStatusEntry(string WatchKey, string GameId, string DisplayName, string ConfigPath, GameWatchSpec Spec, bool Changed);

// Reads the engine's sync output (docs/sync-watcher.md#cli) for the GUI and the tray host alike.
public static class SyncOutput
{
    // The engine arguments in front of every sync call (no status lines, no benchmark, no load corrections), as the GUI
    // sends them.
    public static IReadOnlyList<string> CommonArguments { get; } = ["--status", "false", "--benchmark", "false", "--load-corrections", "false"];

    public static string WatchKey(string gameId, string configPath) => gameId + "|" + configPath;

    // "<game>|<config path>" back to its parts (a path may contain no '|').
    public static (string GameId, string ConfigPath) SplitWatchKey(string watchKey)
    {
        int bar = (watchKey ?? string.Empty).IndexOf('|');
        return bar < 0 ? (watchKey ?? string.Empty, string.Empty) : (watchKey![..bar], watchKey[(bar + 1)..]);
    }

    // --game <id> [--path <config>]
    public static List<string> GameArguments(string watchKey)
    {
        (string gameId, string path) = SplitWatchKey(watchKey);
        List<string> args = ["--game", gameId];
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--path");
            args.Add(path);
        }
        return args;
    }

    // The watched configs of engine sync status (null when the output is no status JSON). Disabled, untracked,
    // missing and not installed games are left out.
    public static List<SyncStatusEntry>? ParseStatus(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJson(output));
            List<SyncStatusEntry> entries = [];
            if (!doc.RootElement.TryGetProperty("Games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
                return entries;
            foreach (JsonElement game in games.EnumerateArray())
            {
                string state = String(game, "State");
                if (!Bool(game, "Installed") || Bool(game, "Disabled") || !Bool(game, "Tracked") || state is "missing" or "not-installed" or "disabled")
                    continue;
                string gameId = String(game, "Game");
                string configPath = String(game, "ConfigPath");
                string key = WatchKey(gameId, configPath);
                List<GameWatchFolder> folders = [];
                if (game.TryGetProperty("Watch", out JsonElement watch) && watch.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement folder in watch.EnumerateArray())
                        folders.Add(new GameWatchFolder(String(folder, "Directory"), Strings(folder, "Filters"), Bool(folder, "IncludeSubdirectories")));
                }
                GameWatchSpec spec = new(key, folders, String(game, "RegistryKey"), Strings(game, "ProcessNames"), Strings(game, "ProcessFolders"), Int(game, "SettleSeconds"));
                string name = String(game, "DisplayName");
                entries.Add(new SyncStatusEntry(key, gameId, name.Length > 0 ? name : gameId, configPath, spec, state == "changed"));
            }
            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The games of engine sync status that are not watched, with the reason (diagnostics: "MSFS2024: untracked (no export
    // or import recorded yet)"). Games that are not installed or that the user does not have are left out (there are
    // many). Empty when the output is no status JSON.
    public static List<string> DescribeUnwatched(string output)
    {
        List<string> lines = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJson(output));
            if (!doc.RootElement.TryGetProperty("Games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
                return lines;
            foreach (JsonElement game in games.EnumerateArray())
            {
                string state = String(game, "State");
                if (!Bool(game, "Installed") || Bool(game, "Disabled") || state is "not-installed" or "disabled")
                    continue;
                if (state == "missing")
                    lines.Add(String(game, "Game") + ": no config found (" + String(game, "ConfigPath") + ")");
                else if (!Bool(game, "Tracked"))
                    lines.Add(String(game, "Game") + ": untracked (no export or import recorded yet; " + String(game, "ConfigPath") + ")");
            }
        }
        catch (JsonException)
        {
        }
        return lines;
    }

    // Whether status (usually for one --game) reports a changed config.
    public static bool StatusSaysChanged(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJson(output));
            return doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array &&
                   games.EnumerateArray().Any(g => String(g, "State") == "changed");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The game's display name and how many changes engine sync diff lists (null when the output is no diff JSON).
    public static (string DisplayName, int Changes)? ParseDiffSummary(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJson(output));
            int count = doc.RootElement.TryGetProperty("Changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array
                ? changes.GetArrayLength()
                : 0;
            string name = String(doc.RootElement, "DisplayName");
            return (name.Length > 0 ? name : String(doc.RootElement, "Game"), count);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What a diff compared, for the log line of a diff without changes ("12 binds JoyPro manages in the game, 12 in the
    // profile, baseline: yes"); empty when the output is no diff JSON.
    public static string DiffCounts(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJson(output));
            JsonElement root = doc.RootElement;
            int Number(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : 0;
            return Number("FileBinds") + " bind(s) JoyPro manages in the game, " + Number("ProfileBinds") + " in the profile, baseline: " + (Bool(root, "Baseline") ? "yes" : "no");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // The JSON payload of engine output: from the first line that starts with '{' or '[' to the last closing bracket.
    public static string ExtractJson(string output)
    {
        string text = output ?? string.Empty;
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int contentStart = lineStart;
            while (contentStart < text.Length && (text[contentStart] == ' ' || text[contentStart] == '\t'))
                contentStart++;
            if (contentStart < text.Length && (text[contentStart] == '{' || text[contentStart] == '['))
            {
                char closer = text[contentStart] == '{' ? '}' : ']';
                int end = text.LastIndexOf(closer);
                if (end > contentStart)
                    return text.Substring(contentStart, end - contentStart + 1);
                break;
            }
            int nextLine = text.IndexOf('\n', lineStart);
            if (nextLine < 0)
                break;
            lineStart = nextLine + 1;
        }
        return text;
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static List<string> Strings(JsonElement element, string name)
    {
        List<string> values = [];
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    values.Add(text);
            }
        }
        return values;
    }
}
