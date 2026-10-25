using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Services;

// Which engine calls the engine server runs (engine/EngineServer.cs, docs/testing.md "Speed"), and after which calls of
// their own process the server is started again. Served: the quick, interactive commands of the relation editor,
// catalog, search, binds and layout pages; each runs exactly as in a process of its own (own arguments, environment,
// output, exit code, the profile as the file has it). Everything else (exports, imports, refreshes, renders, the AI
// assistant, vJoy, ...) keeps its own process; one of those that can change game data makes the app start a new server
// for the next served call, so no kept list is older than the files.
public static class EngineServerCommands
{
    private static readonly Dictionary<string, HashSet<string>> Served = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relations"] = Set("list", "show", "add", "group", "item", "mass", "search", "browse", "catalog", "pair", "functions", "resolve", "rename",
            "function", "aircraft", "check-aircraft", "suggest", "meanings", "action-info", "duplicates", "merge-copies"),
        ["binds"] = Set("list", "show", "set", "clear", "tuning", "detent", "modifier", "unbind"),
        ["profile"] = Set("summary", "load", "save", "games", "validate"),
        ["layout"] = Set("titles", "suggest", "template-inspect"),
        ["devices"] = Set("list", "inputs", "alias", "aliases", "auto-remap"),
        ["games"] = Set("installs", "integrations", "support"),
        ["hud"] = Set("devices"),
        ["backups"] = Set("list"),
        // The AI model's state (read-only: files and the local model server's answer), asked after every suggestion.
        ["llm"] = Set("status"),
        // The DCS export (byte for byte the files of its own process, checked on a copy of a real Saved Games folder):
        // in the server its profile copy and correction lists are ready, the export itself is the rest.
        ["dcs"] = Set("export"),
        // Lay out with AI's kept preferences; its preview only without the local AI model (arrange ask --llm off, a few
        // seconds of planning; with the model it keeps a process of its own, like every AI path).
        ["arrange"] = Set("prefs"),
        // The usage statistics (engine/EngineUsage.cs): the ranking, the Statistics page, pin / exclude, the sources.
        ["usage"] = Set("stats", "statistics", "ranking", "sources", "pin", "unpin", "exclude", "include", "backups"),
    };

    // Commands in a process of their own that change nothing the server keeps (no new server after them).
    private static readonly Dictionary<string, HashSet<string>> Harmless = new(StringComparer.OrdinalIgnoreCase)
    {
        ["version"] = Set(string.Empty),
        ["media"] = Set("*"),
        ["llm"] = Set("*"),
        ["backups"] = Set("list", "show"),
        ["vjoy"] = Set("status"),
        ["support"] = Set("*"),
        // The usage harvest reads game logs into the usage store only (its own process: the first one reads a year of logs).
        ["usage"] = Set("harvest", "restore"),
    };

    // Global options that take a value (engine Program.Run).
    private static readonly HashSet<string> ValueOptions = Set("--profile", "--out", "--editor", "--status", "--progress", "--benchmark", "--load-corrections",
        "--auto-add-db-items", "--remap-missing-devices", "--confirm-device-remap", "--confirm-corrections", "--correction-mode", "--correction-auto-threshold",
        "--correction-report", "--history-note", "--usage-style");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    // The command's area and first word after the global options ("relations", "show").
    public static (string Area, string Action, bool Editor) Command(IReadOnlyList<string> arguments)
    {
        string area = string.Empty, action = string.Empty;
        bool editor = false;
        for (int i = 0; i < arguments.Count; i++)
        {
            string arg = arguments[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;
            if (area.Length == 0 && arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(arg, "--editor", StringComparison.OrdinalIgnoreCase))
                    editor = true;
                if (ValueOptions.Contains(arg))
                    i++;
                continue;
            }
            if (area.Length == 0)
            {
                area = arg;
                continue;
            }
            action = arg.StartsWith("--", StringComparison.Ordinal) ? string.Empty : arg;
            break;
        }
        return (area, action, editor);
    }

    public static bool IsServed(IReadOnlyList<string> arguments)
    {
        (string area, string action, bool editor) = Command(arguments);
        if (!editor && string.Equals(area, "arrange", StringComparison.OrdinalIgnoreCase) && string.Equals(action, "ask", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (string.Equals(arguments[i], "--llm", StringComparison.OrdinalIgnoreCase))
                    return string.Equals(arguments[i + 1], "off", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        // A fix of duplicates in words may start the local AI model: in a process of its own, as every AI path.
        if (!editor && string.Equals(area, "relations", StringComparison.OrdinalIgnoreCase) && string.Equals(action, "duplicates", StringComparison.OrdinalIgnoreCase) &&
            arguments.Any(a => string.Equals(a, "--instruction", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (string.Equals(arguments[i], "--llm", StringComparison.OrdinalIgnoreCase))
                    return string.Equals(arguments[i + 1], "off", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        return !editor && Served.TryGetValue(area, out HashSet<string>? actions) && actions.Contains(action);
    }

    // Areas whose commands of their own process can change the game lists, function tables, device or cockpit lists the
    // server keeps (a refresh of any game is caught by its action word).
    private static readonly HashSet<string> DataAreas = Set("db", "setup", "cockpits", "devices", "games", "relations", "layout", "hud");

    // After this command ran in a process of its own, may the server keep its lists? Exports, imports, backups and the
    // other game commands change game files and the profile, not the lists (the server also checks the list files
    // before every command, engine EngineServer.DataStamp); refreshes and the data areas start a new server.
    public static bool KeepsServer(IReadOnlyList<string> arguments)
    {
        (string area, string action, _) = Command(arguments);
        if (Served.TryGetValue(area, out HashSet<string>? served) && served.Contains(action))
            return true;
        if (Harmless.TryGetValue(area, out HashSet<string>? harmless) && (harmless.Contains("*") || harmless.Contains(action)))
            return true;
        if (action.Contains("refresh", StringComparison.OrdinalIgnoreCase) || area.Contains("refresh", StringComparison.OrdinalIgnoreCase))
            return false;
        return !DataAreas.Contains(area);
    }
}
