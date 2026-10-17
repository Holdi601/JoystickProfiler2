using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace JoyPro.Gui.Services;

// Version, folders and the GitHub repository used for updates and problem reports.
public static class AppInfo
{
    public const string RepositoryOwner = "Holdi601";
    public const string RepositoryName = "JoystickProfiler2";

    public static string Version
    {
        get
        {
            string? informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    // The commit the build was made from (the first 7 characters; empty for a build outside git).
    public static string Commit
    {
        get
        {
            string? informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            int plus = informational?.IndexOf('+') ?? -1;
            if (informational == null || plus < 0)
                return string.Empty;
            string sha = informational[(plus + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
    }

    // The day of the build (yyyy-MM-dd, UTC; JoyPro.Gui.csproj BuildDate).
    public static string BuildDate =>
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? string.Empty;

    // "Built 2026-10-17 from commit a6a72cc, .NET 9.0.10".
    public static string BuildDescription
    {
        get
        {
            string net = Environment.Version.ToString(3);
            if (BuildDate.Length == 0)
                return T("Build date unknown, .NET {0}", net);
            return Commit.Length > 0 ? T("Built {0} from commit {1}, .NET {2}", BuildDate, Commit, net) : T("Built {0}, .NET {1}", BuildDate, net);
        }
    }

    public static string InstallFolder => AppContext.BaseDirectory;

    // JOYPRO_DATA_DIR replaces %LOCALAPPDATA%\JoyPro2 (tests).
    public static string DataFolder
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            string folder = string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2")
                : overrideDir;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    // The GUI's own settings (ui-settings.json, ui-layout.json, hud.json, engine-output.log): %APPDATA%\JoyPro2, or
    // <JOYPRO_DATA_DIR>\settings for tests (a test run never touches the user's GUI settings).
    // The tray host (JoyPro.Tray) reads the same folder.
    public static string SettingsFolder => JoyPro.Watch.JoyProPaths.SettingsFolder;

    public static string CrashReportFolder => EnsureSubfolder("CrashReports");

    // The background logs of the GUI, the engine and JoyPro.Ffb (docs/logging.md): <DataFolder>\Logs, i.e.
    // %LOCALAPPDATA%\JoyPro2\Logs or <JOYPRO_DATA_DIR>\Logs (JOYPRO_LOG_DIR replaces it).
    public static string LogFolder => JoyPro.Logging.JoyProLog.IsStarted ? JoyPro.Logging.JoyProLog.Folder : JoyPro.Logging.JoyProLog.DefaultFolder();

    public static string EnsureSubfolder(string name)
    {
        string folder = Path.Combine(DataFolder, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string IssuesUrl => "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/issues/new";

    public static string LatestReleaseApiUrl => "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";

    public static string OsDescription => System.Runtime.InteropServices.RuntimeInformation.OSDescription + " (" +
                                          System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ")";
}
