using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services;

// Writes a report for every unhandled exception of the GUI (UI thread, tasks, other threads) and for engine crashes
// (the engine writes "engine-*.txt" into the same folder). On the next start the GUI offers to report them.
public static class CrashReporter
{
    private const string SeenMarker = ".seen";

    // Recent engine commands and output, set by the main view model.
    public static Func<string>? ContextProvider { get; set; }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "unhandled exception (terminating: " + e.IsTerminating + ")");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "unobserved task exception");
            e.SetObserved();
        };
    }

    public static string Write(Exception? exception, string context)
    {
        JoyPro.Logging.JoyProLog.Error("Crash", "Crash (" + context + ")", exception);
        JoyPro.Logging.JoyProLog.Flush();
        try
        {
            string path = Path.Combine(AppInfo.CrashReportFolder, "gui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
            StringBuilder sb = new();
            sb.AppendLine("JoyPro2 crash report");
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Version: " + AppInfo.Version);
            sb.AppendLine("OS: " + AppInfo.OsDescription);
            sb.AppendLine(".NET: " + Environment.Version);
            sb.AppendLine("Context: " + context);
            sb.AppendLine("Log: " + AppInfo.LogFolder + " (session " + JoyPro.Logging.JoyProLog.SessionId + ")");
            sb.AppendLine();
            sb.AppendLine(exception?.ToString() ?? "(no exception object)");
            string recent = SafeContext();
            if (recent.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent engine activity:");
                sb.AppendLine(recent);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeContext()
    {
        try
        {
            return ContextProvider?.Invoke() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static List<string> AllReports()
    {
        return Directory.GetFiles(AppInfo.CrashReportFolder, "*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    // Reports the user has not been told about yet.
    public static List<string> UnseenReports()
    {
        string marker = Path.Combine(AppInfo.CrashReportFolder, SeenMarker);
        DateTime seen = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
        return AllReports().Where(f => File.GetLastWriteTimeUtc(f) > seen).ToList();
    }

    public static void MarkSeen()
    {
        string marker = Path.Combine(AppInfo.CrashReportFolder, SeenMarker);
        File.WriteAllText(marker, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
    }
}
