using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Tests.QualityGate;

// The quality gate (docs/testing.md "Quality gate"): invariant checks on REAL data through the real view model and the
// real engine - every game JoyPro lists, every DCS aircraft, every relation of a real profile, budgets for the common
// operations, theme contrast. Opt-in (it takes a while): JOYPRO_QUALITY_GATE=1. tools/quality_gate.ps1 runs it.
//   JOYPRO_QUALITY_GATE_DATA      user (default: a copy of the game lists in %LOCALAPPDATA%\JoyPro\GameData, or the
//                                 folder JOYPRO_QUALITY_GATE_GAMEDATA names) | shipped (only what the program folder has)
//   JOYPRO_QUALITY_GATE_PROFILE   a .pr0file; the gate works on a copy (without it the profile checks are skipped)
//   JOYPRO_QUALITY_GATE_OUT       output folder (report.md, findings.json, timings.csv, engine-calls.csv, renders)
//   JOYPRO_QUALITY_GATE_ENGINE    engine.exe (default: engine\bin\x64\<test configuration>\engine.exe of the repo)
//   JOYPRO_QUALITY_GATE_PLANES    "all" (default) or a number: how many DCS aircraft the catalog check opens
//   JOYPRO_QUALITY_GATE_RELATIONS "all" (default) or a number: how many relations of the profile the editor opens
// Pages are laid out and drawn in roots that are never shown (HiddenWindow): no window reaches the desktop or takes the
// focus; one that opens anyway is closed at once and reported (GateSession).
// Nothing outside the output folder is written: game folders are only read (exports go to copies or run as dry runs).
internal static class Gate
{
    public static bool Enabled => Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE") is "1" or "true" or "yes";

    public static string DataMode => (Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_DATA") ?? "user").Trim().ToLowerInvariant() == "shipped" ? "shipped" : "user";

    private static readonly Lazy<string> OutDirLazy = new(() =>
    {
        string? configured = Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_OUT");
        string dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "quality-gate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
            : configured.Trim();
        Directory.CreateDirectory(dir);
        return dir;
    });

    public static string OutDir => OutDirLazy.Value;

    public static string EnginePath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_ENGINE");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim();
            return Path.Combine(RepoRoot(), "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        }
    }

    public static string? SourceProfile
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_PROFILE");
            return string.IsNullOrWhiteSpace(value) || !File.Exists(value.Trim()) ? null : value.Trim();
        }
    }

    public static int Limit(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : int.MaxValue;
    }

    public static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    // ------------------------------------------------------------------ data folder (prepared once per run)

    private static readonly object PrepareLock = new();
    private static string? dataDir;

    // One JOYPRO_DATA_DIR for the whole run: the copied game lists (user) or an empty folder the engine fills from the
    // program folder on its first start (shipped). Never the user's own folder.
    public static string DataDir
    {
        get
        {
            lock (PrepareLock)
            {
                if (dataDir != null)
                    return dataDir;
                string dir = Path.Combine(OutDir, "data-" + DataMode);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);
                if (DataMode == "user")
                {
                    string source = Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_GAMEDATA") is { Length: > 0 } configured
                        ? configured
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro", "GameData");
                    // The game lists and DCS clean profiles; not the cockpit pictures (gigabytes, no part of a check).
                    foreach (string part in new[] { "DB", "CleanProfile", "KeyboardCleanProfile" })
                    {
                        string from = Path.Combine(source, part);
                        if (Directory.Exists(from))
                            CopyFolder(from, Path.Combine(dir, "GameData", part));
                    }
                }
                dataDir = dir;
                return dir;
            }
        }
    }

    public static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (string sub in Directory.GetDirectories(from))
            CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    // A fresh copy of the source profile for one check (checks that edit it never see each other's edits).
    public static string? CopyProfile(string tag)
    {
        string? source = SourceProfile;
        if (source == null)
            return null;
        string dir = Path.Combine(OutDir, "profiles");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, tag + ".pr0file");
        File.Copy(source, copy, overwrite: true);
        return copy;
    }

    // An empty profile for checks that need one to write to but no relations (suggestions on a clean slate).
    public static string EmptyProfile(string tag)
    {
        string dir = Path.Combine(OutDir, "profiles");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, tag + ".pr0file");
        if (File.Exists(path))
            File.Delete(path);
        return path;
    }

    // ------------------------------------------------------------------ findings and timings

    private static readonly object ReportLock = new();
    private static readonly List<Finding> findings = [];
    private static readonly List<Timing> timings = [];
    private static readonly List<string> notes = [];
    private static readonly Dictionary<string, string> invariantNames = new(StringComparer.Ordinal)
    {
        ["I1"] = "Catalog and editor show actions for every game (and every DCS aircraft)",
        ["I2"] = "Action titles are readable and every action has a category",
        ["I3"] = "Every game on the Start page / games list has catalog actions findable in search",
        ["I4"] = "Common searches return readable results per game that has them",
        ["I5"] = "Suggestions for typical relations stay non-empty after a second game and after a reload",
        ["I6"] = "Every relation of the profile shows readable titles (editor, binds peek, layout table)",
        ["I7"] = "Performance budgets",
        ["I8"] = "Theme contrast (Light and Dark)",
        ["I9"] = "Games and features named in the UI have an implementation",
        ["I10"] = "Layout: no overlapping controls, no cut-off text, no dead pre-filled search (every page, size, scale, theme)",
        ["I11"] = "Names, slang, paraphrases, everyday sentences and German find their function on every entry point (offline)",
    };

    public sealed record Finding(string Invariant, string Rule, string Game, string Subject, string Detail);

    public sealed record Timing(string Operation, string Subject, double Milliseconds, double BudgetMs, int EngineCalls, string Detail)
    {
        public bool OverBudget => BudgetMs > 0 && Milliseconds > BudgetMs;
    }

    public static void Fail(string invariant, string rule, string game, string subject, string detail = "")
    {
        lock (ReportLock)
            findings.Add(new Finding(invariant, rule, game ?? string.Empty, subject ?? string.Empty, detail ?? string.Empty));
    }

    public static void Note(string text)
    {
        lock (ReportLock)
            notes.Add(text);
    }

    public static Timing Time(string operation, string subject, double ms, int engineCalls, string detail = "")
    {
        double budget = Budgets.For(operation);
        Timing timing = new(operation, subject, ms, budget, engineCalls, detail);
        lock (ReportLock)
            timings.Add(timing);
        if (timing.OverBudget)
            Fail("I7", "over budget: " + operation, string.Empty, subject, ms.ToString("0", CultureInfo.InvariantCulture) + " ms > " + budget.ToString("0", CultureInfo.InvariantCulture) + " ms budget, " + engineCalls + " engine call(s)" + (detail.Length > 0 ? "; " + detail : string.Empty));
        return timing;
    }

    public static IReadOnlyList<Finding> FindingsOf(string invariant)
    {
        lock (ReportLock)
            return findings.Where(f => f.Invariant == invariant).ToList();
    }

    // Each check ends with this: the report is written again (every check adds to the same files) and the check fails
    // with a summary of its findings.
    public static void Finish(string invariant)
    {
        WriteReport();
        IReadOnlyList<Finding> own = FindingsOf(invariant);
        if (own.Count == 0)
            return;
        StringBuilder message = new();
        message.AppendLine(invariant + " " + invariantNames[invariant] + ": " + own.Count + " finding(s). Report: " + Path.Combine(OutDir, "report.md"));
        foreach (IGrouping<string, Finding> rule in own.GroupBy(f => f.Rule).OrderByDescending(g => g.Count()))
        {
            message.AppendLine("  " + rule.Key + ": " + rule.Count() + " (" + rule.Select(f => f.Game).Where(g => g.Length > 0).Distinct().Count() + " games)");
            foreach (Finding f in rule.Take(5))
                message.AppendLine("    " + Describe(f));
        }
        throw new Xunit.Sdk.XunitException(message.ToString());
    }

    private static string Describe(Finding f) =>
        string.Join(" | ", new[] { f.Game, f.Subject, f.Detail }.Where(t => t.Length > 0));

    public static void WriteReport()
    {
        lock (ReportLock)
        {
            StringBuilder md = new();
            md.AppendLine("# JoyPro quality gate");
            md.AppendLine();
            md.AppendLine("- Run: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", data: " + DataMode + ", profile: " + (SourceProfile != null ? Path.GetFileName(SourceProfile) : "(none)") +
                          ", engine: " + EngineVersion());
            md.AppendLine("- Findings: " + findings.Count + " in " + findings.Select(f => f.Invariant).Distinct().Count() + " invariant(s)");
            md.AppendLine();
            foreach ((string id, string name) in invariantNames)
            {
                List<Finding> own = findings.Where(f => f.Invariant == id).ToList();
                md.AppendLine("## " + id + " " + name + " - " + (own.Count == 0 ? "no findings" : own.Count + " finding(s)"));
                md.AppendLine();
                foreach (IGrouping<string, Finding> rule in own.GroupBy(f => f.Rule).OrderByDescending(g => g.Count()))
                {
                    List<IGrouping<string, Finding>> games = rule.GroupBy(f => f.Game).OrderByDescending(g => g.Count()).ToList();
                    md.AppendLine("### " + rule.Key + " (" + rule.Count() + ")");
                    md.AppendLine();
                    foreach (IGrouping<string, Finding> game in games)
                    {
                        // Findings without a game (layout, contrast, budgets): one line each.
                        if (game.Key.Length == 0)
                        {
                            foreach (Finding f in game.Take(150))
                                md.AppendLine("- " + Escape(string.Join(" - ", new[] { f.Subject, f.Detail }.Where(t => t.Length > 0))));
                            if (game.Count() > 150)
                                md.AppendLine("- ... " + (game.Count() - 150) + " more in findings.json");
                            continue;
                        }
                        md.AppendLine("- " + (game.Key.Length > 0 ? "**" + game.Key + "** " : string.Empty) + game.Count() + ": " +
                                      string.Join("; ", game.Take(8).Select(f => Escape(string.Join(" - ", new[] { f.Subject, f.Detail }.Where(t => t.Length > 0))))) +
                                      (game.Count() > 8 ? "; ..." : string.Empty));
                    }
                    md.AppendLine();
                }
            }
            if (timings.Count > 0)
            {
                md.AppendLine("## Timings (budgets in Budgets.cs)");
                md.AppendLine();
                md.AppendLine("| Operation | Subject | ms | Budget ms | Engine calls | Detail |");
                md.AppendLine("|---|---|---:|---:|---:|---|");
                foreach (Timing t in timings.OrderBy(t => t.Operation, StringComparer.Ordinal).ThenByDescending(t => t.Milliseconds).GroupBy(t => t.Operation).SelectMany(g => g.Take(25)))
                    md.AppendLine("| " + t.Operation + " | " + Escape(t.Subject) + " | " + t.Milliseconds.ToString("0", CultureInfo.InvariantCulture) + (t.OverBudget ? " **over**" : string.Empty) + " | " +
                                  t.BudgetMs.ToString("0", CultureInfo.InvariantCulture) + " | " + t.EngineCalls + " | " + Escape(t.Detail) + " |");
                md.AppendLine();
            }
            if (notes.Count > 0)
            {
                md.AppendLine("## Notes");
                md.AppendLine();
                foreach (string note in notes)
                    md.AppendLine("- " + Escape(note));
            }
            File.WriteAllText(Path.Combine(OutDir, "report.md"), md.ToString());
            File.WriteAllText(Path.Combine(OutDir, "findings.json"), JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
            StringBuilder csv = new("operation,subject,ms,budget_ms,engine_calls,detail\n");
            foreach (Timing t in timings)
                csv.AppendLine(string.Join(",", Csv(t.Operation), Csv(t.Subject), t.Milliseconds.ToString("0", CultureInfo.InvariantCulture), t.BudgetMs.ToString("0", CultureInfo.InvariantCulture), t.EngineCalls, Csv(t.Detail)));
            File.WriteAllText(Path.Combine(OutDir, "timings.csv"), csv.ToString());
        }
    }

    private static string Escape(string text) => (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Csv(string text) => "\"" + (text ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static string? engineVersion;

    private static string EngineVersion()
    {
        if (engineVersion != null)
            return engineVersion;
        try
        {
            engineVersion = FileVersionInfo.GetVersionInfo(EnginePath).ProductVersion ?? "?";
        }
        catch (Exception)
        {
            engineVersion = "?";
        }
        return engineVersion;
    }

    // ------------------------------------------------------------------ readable text rules

    // "Input.SpitfireLFMkIX.flaps_1" (a DCS localization key), any dotted path without spaces.
    private static readonly Regex LuaKey = new(@"^[A-Za-z_][\w-]*(\.[\w-]+){2,}$", RegexOptions.CultureInvariant);
    // "Throttle", "Landing Gear", "Collective": words a person reads.
    private static readonly Regex PlainWords = new(@"^[A-Z]?[a-z]+( [A-Za-z][a-z]*| \d+)*$", RegexOptions.CultureInvariant);
    // DCS's encoded ids: "d3014pnilu3014cd16vd1vpnilvu0", "d540pnilu543cdnilvdnilvpnilvunil".
    private static readonly Regex DcsEncodedId = new(@"^d-?\d+p(nil|-?[\d.]+)u", RegexOptions.CultureInvariant);
    private static readonly Regex HexId = new(@"^(0x)?[0-9A-Fa-f]{8,}$|^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$", RegexOptions.CultureInvariant);
    // "pitch_minus", "KEY_MASTER_CAUTION", "v_strafe_up": a program identifier, no words for a person.
    private static readonly Regex Identifier = new(@"^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", RegexOptions.CultureInvariant);
    // "BASIC_CONTROL_HELICOPTER" as a category.
    private static readonly Regex RawCategory = new(@"^[A-Z0-9]+(_[A-Z0-9]+)+$|^[a-z0-9]+(_[a-z0-9]+)+$", RegexOptions.CultureInvariant);

    // Why a title a person sees is not readable, or null when it is.
    public static string? UnreadableTitle(string? title, string? id)
    {
        string text = (title ?? string.Empty).Trim();
        string key = (id ?? string.Empty).Trim();
        if (text.Length == 0)
            return "empty title";
        // The id itself as the title: fine only when the id is words ("Throttle", "SAS Pitch Off" in VTOL VR).
        if (key.Length > 0 && string.Equals(text, key, StringComparison.OrdinalIgnoreCase) && !PlainWords.IsMatch(text) && !(text.Contains(' ') && !text.Contains('_') && !text.Contains('.')))
            return "title is the id";
        if (DcsEncodedId.IsMatch(text))
            return "title is an encoded DCS id";
        if (HexId.IsMatch(text))
            return "title is a hex/GUID id";
        if (LuaKey.IsMatch(text))
            return "title is a lua key / dotted path";
        if (Identifier.IsMatch(text))
            return "title is a program identifier (snake_case)";
        return null;
    }

    public static string? UnreadableCategory(string? category)
    {
        string text = (category ?? string.Empty).Trim();
        if (text.Length == 0)
            return "no category";
        foreach (string part in text.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (RawCategory.IsMatch(part))
                return "category is a raw key";
        }
        return null;
    }

    public static string Short(string text, int max = 90)
    {
        string t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "...";
    }
}

// Every engine call of a session with its time: an operation's budget line says how many engine processes it took.
internal sealed class RecordingEngineClient : IEngineCliClient
{
    private readonly EngineCliClient inner = new();
    private readonly object sync = new();
    private readonly List<(DateTime Start, double Ms, int Exit, string Args)> calls = [];

    public int Count
    {
        get
        {
            lock (sync)
                return calls.Count;
        }
    }

    public IReadOnlyList<(DateTime Start, double Ms, int Exit, string Args)> Calls
    {
        get
        {
            lock (sync)
                return calls.ToList();
        }
    }

    public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        DateTime start = DateTime.Now;
        Stopwatch watch = Stopwatch.StartNew();
        EngineCommandResult result = await inner.RunAsync(engineExePath, arguments, cancellationToken);
        Record(start, watch.Elapsed.TotalMilliseconds, result.ExitCode, arguments);
        return result;
    }

    public async Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
    {
        DateTime start = DateTime.Now;
        Stopwatch watch = Stopwatch.StartNew();
        EngineCommandResult result = await ((IEngineCliClient)inner).RunStreamingAsync(engineExePath, arguments, options, cancellationToken);
        Record(start, watch.Elapsed.TotalMilliseconds, result.ExitCode, arguments);
        return result;
    }

    private void Record(DateTime start, double ms, int exit, IReadOnlyList<string> arguments)
    {
        // The command without the GUI's fixed options (--status, --benchmark, --load-corrections, the profile path).
        List<string> shown = [];
        for (int i = 0; i < arguments.Count; i++)
        {
            string a = arguments[i];
            if (a is "--status" or "--benchmark" or "--load-corrections" or "--profile" or "--history-note" or "--auto-add-db-items" or "--remap-missing-devices"
                or "--confirm-device-remap" or "--correction-mode" or "--confirm-corrections" or "--correction-auto-threshold")
            {
                i++;
                continue;
            }
            shown.Add(a.Contains(' ') ? "\"" + a + "\"" : a);
        }
        lock (sync)
            calls.Add((start, ms, exit, string.Join(" ", shown)));
        string line = string.Join(",", start.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), ms.ToString("0", CultureInfo.InvariantCulture), exit,
            "\"" + string.Join(" ", shown).Replace("\"", "\"\"") + "\"") + Environment.NewLine;
        lock (FileLock)
            File.AppendAllText(Path.Combine(Gate.OutDir, "engine-calls.csv"), line);
    }

    private static readonly object FileLock = new();
}
