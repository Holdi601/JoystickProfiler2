using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

public sealed class ProblemReport
{
    public string ZipPath { get; init; } = string.Empty;
    public string IssueUrl { get; init; } = string.Empty;

    // The zip's entries ("report.txt", "logs/gui-20261011.log", ...).
    public IReadOnlyList<string> Entries { get; init; } = [];
}

// Bundles a user's problem description with diagnostics into one zip the user can look into before sending it
// (docs/logging.md): report.txt (description, versions, system, diagnostics, list of contents), the background logs of
// the last days (logs/), crash reports (crash-reports/), the engine output of this session (engine-output.log) and,
// only when the user ticks it, the profile (profile/, unchanged). Every text file goes through LogScrubber (user folder,
// user name, PC name, e-mail, passwords/tokens, Steam ids). Nothing is uploaded: the GUI shows the zip in Explorer and
// the user attaches it to a GitHub issue (prefilled link, opened only on request) or sends it to the developer.
public static class ProblemReporter
{
    public const int DefaultLogDays = 3;
    public const long MaxLogBytes = 60L * 1024 * 1024;
    public const int MaxCrashReports = 10;

    // Where reports go when the caller names no folder (Documents\JoyPro2 Problem Reports); the tests point it to a
    // temp folder.
    public static string? FolderOverride { get; set; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro2 Problem Reports");

    public static ProblemReport Create(
        string description,
        string diagnostics,
        string? profilePath,
        bool includeProfile,
        IEnumerable<string> crashReports,
        string? outputFolder = null,
        string? logFolder = null,
        int logDays = DefaultLogDays,
        IEnumerable<string>? extraFiles = null,
        DateTime? now = null)
    {
        JoyProLog.Flush();
        DateTime time = now ?? DateTime.Now;
        string folder = outputFolder ?? FolderOverride ?? DefaultFolder;
        Directory.CreateDirectory(folder);
        string stamp = time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string zipPath = Path.Combine(folder, "JoyPro2-problem-" + stamp + ".zip");
        for (int n = 2; File.Exists(zipPath); n++)
            zipPath = Path.Combine(folder, "JoyPro2-problem-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip");

        List<string> logs = SelectLogs(logFolder, logDays, time);
        List<string> crashes = (crashReports ?? []).Where(File.Exists).Take(MaxCrashReports).ToList();
        List<string> extras = (extraFiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList();
        bool attachProfile = includeProfile && !string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath);

        List<string> entries = ["report.txt"];
        entries.AddRange(logs.Select(l => "logs/" + Path.GetFileName(l)));
        entries.AddRange(crashes.Select(c => "crash-reports/" + Path.GetFileName(c)));
        entries.AddRange(extras.Select(Path.GetFileName).Where(n => n != null).Select(n => n!));
        if (attachProfile)
            entries.Add("profile/" + Path.GetFileName(profilePath));

        StringBuilder report = new();
        report.AppendLine(T("JoyPro2 problem report"));
        report.AppendLine(T("Time: {0} (local), UTC offset {1}", time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), TimeZoneInfo.Local.GetUtcOffset(time).ToString(@"hh\:mm", CultureInfo.InvariantCulture)));
        report.AppendLine(T("Version: {0}", AppInfo.Version));
        report.AppendLine(T("OS: {0}", AppInfo.OsDescription));
        report.AppendLine(T("System: {0}", JoyProLog.SystemDescription()));
        report.AppendLine(T("Log session: {0} | logs of the last {1} day(s) from {2}", (JoyProLog.SessionId.Length > 0 ? JoyProLog.SessionId : "(logging not started)"), logDays.ToString(CultureInfo.InvariantCulture), (logFolder ?? "(none)")));
        report.AppendLine();
        report.AppendLine(T("What happened:"));
        report.AppendLine(string.IsNullOrWhiteSpace(description) ? T("(no description)") : description.Trim());
        report.AppendLine();
        report.AppendLine(T("Diagnostics:"));
        report.AppendLine(diagnostics);
        report.AppendLine();
        report.AppendLine(T("Contents of this zip:"));
        foreach (string entry in entries)
            report.AppendLine("  " + entry);
        report.AppendLine(attachProfile ? T("  (the profile is attached unchanged)") : T("  (no profile attached)"));
        report.AppendLine();
        report.AppendLine(T("Personal data is replaced in all text files: the user folder by %USERPROFILE%, the Windows user name by <user>,"));
        report.AppendLine(T("the PC name by <pc>, e-mail addresses by <email>, passwords and tokens by <redacted>, Steam ids by steam#<hash>."));

        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddText(zip, "report.txt", LogScrubber.Scrub(report.ToString()));
            foreach (string log in logs)
                AddScrubbedFile(zip, "logs/" + Path.GetFileName(log), log);
            foreach (string crash in crashes)
                AddScrubbedFile(zip, "crash-reports/" + Path.GetFileName(crash), crash);
            foreach (string extra in extras)
                AddScrubbedFile(zip, Path.GetFileName(extra), extra);
            if (attachProfile)
                zip.CreateEntryFromFile(profilePath!, "profile/" + Path.GetFileName(profilePath));
        }

        JoyProLog.Info("Report", "Problem report created: " + zipPath + " (" + entries.Count.ToString(CultureInfo.InvariantCulture) + " entries, profile " +
                                 (attachProfile ? "attached" : "not attached") + ")");

        string title = T("Problem: {0}", FirstLine(LogScrubber.Scrub(description ?? string.Empty)));
        string body = "**What happened**\n" + (string.IsNullOrWhiteSpace(description) ? T("(please describe)") : LogScrubber.Scrub(description.Trim())) +
                      "\n\n**Version:** " + AppInfo.Version + "\n**OS:** " + AppInfo.OsDescription +
                      "\n\nPlease attach `" + Path.GetFileName(zipPath) + "` (in Documents\\JoyPro2 Problem Reports).";
        string url = AppInfo.IssuesUrl + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
        return new ProblemReport { ZipPath = zipPath, IssueUrl = url, Entries = entries };
    }

    // The *.log files of the folder written in the last `days` days, newest first, together at most MaxLogBytes.
    public static List<string> SelectLogs(string? logFolder, int days, DateTime now)
    {
        List<string> selected = [];
        if (string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
            return selected;
        DateTime cutoff = now.ToUniversalTime() - TimeSpan.FromDays(Math.Max(1, days));
        long total = 0;
        foreach (FileInfo file in new DirectoryInfo(logFolder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (file.LastWriteTimeUtc < cutoff)
                continue;
            if (total + file.Length > MaxLogBytes && selected.Count > 0)
                continue;
            total += file.Length;
            selected.Add(file.FullName);
        }
        return selected;
    }

    private static string FirstLine(string text)
    {
        string first = (text ?? string.Empty).Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        if (first.Length == 0)
            return T("(no title)");
        return first.Length > 80 ? first[..80] + "..." : first;
    }

    // Log files are still being written by running JoyPro processes: read them shared.
    private static void AddScrubbedFile(ZipArchive zip, string name, string path)
    {
        string text;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = T("(could not be read: {0})", ex.Message);
        }
        AddText(zip, name, LogScrubber.Scrub(text));
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
