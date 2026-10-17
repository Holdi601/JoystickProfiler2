using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.Gui.Services;
using JoyPro.Logging;
using Xunit;

namespace JoyPro.Gui.Tests;

[SupportedOSPlatform("windows")]
[Collection("JoyProDataDir")]
public sealed class AppServicesTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-appservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousDataDir = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public AppServicesTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousDataDir);
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.0.3-beta.1", "2.0.3")]
    [InlineData("V10.4", "10.4.0")]
    public void ParseVersion_ReadsReleaseTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Fact]
    public void ParseVersion_RejectsNonVersions()
    {
        Assert.Null(UpdateService.ParseVersion("latest"));
    }

    [Fact]
    public void CrashReporter_WritesReportsThatStayUnseenUntilMarked()
    {
        CrashReporter.ContextProvider = () => "engine devices list -> exit 0";
        string path = CrashReporter.Write(new InvalidOperationException("boom"), "test");

        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.Contains("engine devices list", text, StringComparison.Ordinal);
        Assert.Contains(path, CrashReporter.UnseenReports());

        CrashReporter.MarkSeen();
        Assert.Empty(CrashReporter.UnseenReports());
    }

    [Fact]
    public void ProblemReporter_ZipsDescriptionDiagnosticsAndCrashes()
    {
        string crash = CrashReporter.Write(new Exception("earlier crash"), "test");
        string profile = Path.Combine(temp, "my.pr0file");
        File.WriteAllText(profile, "{}");

        ProblemReport report = ProblemReporter.Create("Export to DCS lost my binds\nsecond line", "diag text", profile, includeProfile: false, [crash], Path.Combine(temp, "reports"));

        using ZipArchive zip = ZipFile.OpenRead(report.ZipPath);
        string[] names = zip.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("report.txt", names);
        Assert.Contains("crash-reports/" + Path.GetFileName(crash), names);
        Assert.DoesNotContain(names, n => n.StartsWith("profile/", StringComparison.Ordinal));
        using (StreamReader reader = new(zip.GetEntry("report.txt")!.Open()))
        {
            string text = reader.ReadToEnd();
            Assert.Contains("Export to DCS lost my binds", text, StringComparison.Ordinal);
            Assert.Contains("diag text", text, StringComparison.Ordinal);
        }
        Assert.StartsWith(AppInfo.IssuesUrl + "?title=Problem%3A%20Export%20to%20DCS%20lost%20my%20binds", report.IssueUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemReporter_BundlesRecentScrubbedLogsCrashesEngineOutputAndOptionalProfile()
    {
        LogScrubber.Configure(@"C:\Users\jdoe", "jdoe", "GAMINGRIG");
        try
        {
            DateTime now = DateTime.Now;
            string logs = Path.Combine(temp, "logs");
            Directory.CreateDirectory(logs);
            string recent = Path.Combine(logs, "engine-" + now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            File.WriteAllText(recent, @"INFO engine export to C:\Users\jdoe\Saved Games\DCS by jdoe, mail jdoe@example.com, steam 76561198012345678");
            string old = Path.Combine(logs, "gui-20200101.log");
            File.WriteAllText(old, "old line");
            File.SetLastWriteTime(old, now.AddDays(-10));
            string notALog = Path.Combine(logs, "log-settings.txt");
            File.WriteAllText(notALog, "retention-days=7");
            string engineOutput = Path.Combine(temp, "engine-output.log");
            File.WriteAllText(engineOutput, @"$ engine --profile C:\Users\jdoe\Documents\JoyPro\my.pr0file binds list");
            string crash = CrashReporter.Write(new Exception(@"crash in C:\Users\jdoe\x"), "test");
            string profile = Path.Combine(temp, "my.pr0file");
            File.WriteAllText(profile, "{\"binary\":\"unchanged C:\\\\Users\\\\jdoe\"}");

            ProblemReport report = ProblemReporter.Create(@"DCS export lost binds (password=hunter2) in C:\Users\jdoe\Saved Games", "diag for jdoe", profile,
                includeProfile: true, [crash], Path.Combine(temp, "reports"), logFolder: logs, logDays: 3, extraFiles: [engineOutput], now: now);

            using ZipArchive zip = ZipFile.OpenRead(report.ZipPath);
            string[] names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "crash-reports/" + Path.GetFileName(crash),
                "engine-output.log",
                "logs/" + Path.GetFileName(recent),
                "profile/my.pr0file",
                "report.txt"
            }, names);
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), report.Entries.OrderBy(n => n, StringComparer.Ordinal));

            string reportText = ReadEntry(zip, "report.txt");
            Assert.Contains("What happened:", reportText, StringComparison.Ordinal);
            Assert.Contains(@"DCS export lost binds (password=<redacted>) in %USERPROFILE%\Saved Games", reportText, StringComparison.Ordinal);
            Assert.Contains("diag for <user>", reportText, StringComparison.Ordinal);
            Assert.Contains("Contents of this zip:", reportText, StringComparison.Ordinal);
            Assert.Contains("logs/" + Path.GetFileName(recent), reportText, StringComparison.Ordinal);

            string logText = ReadEntry(zip, "logs/" + Path.GetFileName(recent));
            Assert.Contains(@"%USERPROFILE%\Saved Games\DCS by <user>, mail <email>, steam steam#", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("76561198012345678", logText, StringComparison.Ordinal);
            Assert.Contains(@"%USERPROFILE%\Documents\JoyPro\my.pr0file", ReadEntry(zip, "engine-output.log"), StringComparison.Ordinal);
            Assert.DoesNotContain("jdoe", ReadEntry(zip, "crash-reports/" + Path.GetFileName(crash)), StringComparison.OrdinalIgnoreCase);
            // The profile is attached as it is (binary profiles cannot be scrubbed; the user decides).
            Assert.Contains(@"unchanged C:\\Users\\jdoe", ReadEntry(zip, "profile/my.pr0file"), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.IssueUrl, StringComparison.Ordinal);

            ProblemReport withoutProfile = ProblemReporter.Create("second", "diag", profile, includeProfile: false, [], Path.Combine(temp, "reports"), logFolder: logs, now: now);
            using ZipArchive second = ZipFile.OpenRead(withoutProfile.ZipPath);
            Assert.NotEqual(report.ZipPath, withoutProfile.ZipPath);
            Assert.DoesNotContain(second.Entries, e => e.FullName.StartsWith("profile/", StringComparison.Ordinal));
        }
        finally
        {
            JoyPro.Logging.LogScrubber.Reset();
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using StreamReader reader = new(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void RequirementsChecker_ReportsTheEngineComponents()
    {
        var items = RequirementsChecker.Check(temp);
        Assert.Equal([".NET Framework 4.8", "JoyPro engine files"], items.Select(i => i.Name).ToArray());
        Assert.False(items[1].Ok);
        Assert.Contains("engine.exe is missing", items[1].Details, StringComparison.Ordinal);
        Assert.True(items[0].DownloadUrl.Length > 0);
    }
}
