using System.Globalization;
using JoyPro.Logging;

namespace JoyPro.Ffb.Core.Tests;

[CollectionDefinition("JoyProLog", DisableParallelization = true)]
public sealed class JoyProLogCollection
{
}

// The shared background log (engine\JoyProLog.cs, docs/logging.md): retention, caps, line format, correlation and the
// privacy scrubbing. Everything happens in a temp folder; the user's log folder is never touched.
[Collection("JoyProLog")]
public sealed class LoggingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "joypro-logtests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _savedVariables = new();

    public LoggingTests()
    {
        Directory.CreateDirectory(_temp);
        foreach (var name in new[] { JoyProLog.SessionVariable, JoyProLog.ParentVariable, JoyProLog.RetentionVariable, JoyProLog.LevelVariable, JoyProLog.FolderVariable })
            _savedVariables[name] = Environment.GetEnvironmentVariable(name);
        // Crash report cleanup only runs for the default folder layout; a JOYPRO_LOG_DIR keeps it away from real folders.
        Environment.SetEnvironmentVariable(JoyProLog.FolderVariable, Path.Combine(_temp, "Logs"));
    }

    public void Dispose()
    {
        JoyProLog.Stop();
        JoyProLog.MaxFileBytes = JoyProLog.DefaultMaxFileBytes;
        LogScrubber.Reset();
        foreach (var pair in _savedVariables)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            Directory.Delete(_temp, true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeFile(string folder, string name, DateTime lastWriteUtc, int bytes = 10)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Cleanup_DeletesLogsOlderThanTheRetentionAndKeepsRecentOnes()
    {
        var folder = Path.Combine(_temp, "retention");
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var old = MakeFile(folder, "engine-20261001.log", now.AddDays(-10));
        var edge = MakeFile(folder, "gui-20261003.log", now.AddDays(-8));
        var recent = MakeFile(folder, "gui-20261010.log", now.AddDays(-1));
        var today = MakeFile(folder, "ffb-20261011.log", now.AddMinutes(-5));
        var settings = MakeFile(folder, JoyProLog.SettingsFileName, now.AddDays(-100));

        var deleted = JoyProLog.Cleanup(folder, 7, JoyProLog.DefaultMaxTotalBytes, now);

        Assert.Equal(new[] { edge, old }.OrderBy(p => p), deleted.OrderBy(p => p));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(edge));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(settings)); // only *.log files are logs
    }

    [Fact]
    public void Cleanup_KeepsTheNewestFilesWithinTheTotalSizeCap()
    {
        var folder = Path.Combine(_temp, "cap");
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var newest = MakeFile(folder, "gui-20261011.log", now.AddHours(-1), 600);
        var middle = MakeFile(folder, "gui-20261010.log", now.AddHours(-20), 300);
        var oldest = MakeFile(folder, "gui-20261009.log", now.AddHours(-40), 300);

        var deleted = JoyProLog.Cleanup(folder, 7, 1000, now);

        Assert.Equal(new[] { oldest }, deleted);
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(middle));
    }

    [Fact]
    public void Cleanup_ClampsTheRetentionToAtLeastOneDay()
    {
        var folder = Path.Combine(_temp, "clamp");
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var recent = MakeFile(folder, "gui-20261011.log", now.AddHours(-12));

        Assert.Empty(JoyProLog.Cleanup(folder, 0, JoyProLog.DefaultMaxTotalBytes, now));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void CleanupIfDue_RunsAtMostOnceAnHour()
    {
        var folder = Path.Combine(_temp, "due");
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        MakeFile(folder, "gui-20261001.log", now.AddDays(-10));
        Assert.Single(JoyProLog.CleanupIfDue(folder, 7, now));

        var second = MakeFile(folder, "gui-20261002.log", now.AddDays(-9));
        Assert.Empty(JoyProLog.CleanupIfDue(folder, 7, now.AddMinutes(30)));
        Assert.True(File.Exists(second));
        Assert.Contains(second, JoyProLog.CleanupIfDue(folder, 7, now.AddHours(2)));
    }

    [Fact]
    public void CrashReports_AreKeptLongerThanLogs()
    {
        var folder = Path.Combine(_temp, "CrashReports");
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var tenDays = MakeFile(folder, "gui-20261001-120000-000.txt", now.AddDays(-10));
        var fortyDays = MakeFile(folder, "engine-20260901-120000-000.txt", now.AddDays(-40));

        var deleted = JoyProLog.CleanupCrashReports(folder, Math.Max(7, JoyProLog.CrashReportMinRetentionDays), now);

        Assert.Equal(new[] { fortyDays }, deleted);
        Assert.True(File.Exists(tenDays));
    }

    [Fact]
    public void Settings_RoundTripClampAndEnvironmentOverride()
    {
        var folder = Path.Combine(_temp, "settings");
        Assert.Equal(JoyProLog.DefaultRetentionDays, JoyProLogSettings.Read(folder).RetentionDays);
        Assert.Equal(JoyProLogLevel.Debug, JoyProLogSettings.Read(folder).Level);

        new JoyProLogSettings { RetentionDays = 14, Level = JoyProLogLevel.Info }.Write(folder);
        var read = JoyProLogSettings.Read(folder);
        Assert.Equal(14, read.RetentionDays);
        Assert.Equal(JoyProLogLevel.Info, read.Level);

        new JoyProLogSettings { RetentionDays = 5000 }.Write(folder);
        Assert.Equal(JoyProLog.MaxRetentionDays, JoyProLogSettings.Read(folder).RetentionDays);

        Environment.SetEnvironmentVariable(JoyProLog.RetentionVariable, "2");
        Assert.Equal(2, JoyProLogSettings.Read(folder).RetentionDays);
    }

    // The default went from 7 to 30 days (2.4.3): a file of an older version with the old default reads as 30, the
    // same 7 chosen by the user (retention-chosen=true) and any other value stay.
    [Fact]
    public void Settings_OldDefaultBecomesTheNewDefault_AChosenValueStays()
    {
        var folder = Path.Combine(_temp, "settings-migration");
        Directory.CreateDirectory(folder);
        Assert.Equal(30, JoyProLog.DefaultRetentionDays);
        Assert.Equal(200L * 1024 * 1024, JoyProLog.DefaultMaxTotalBytes);
        File.WriteAllText(Path.Combine(folder, JoyProLog.SettingsFileName), "# old\r\nretention-days=7\r\nlevel=debug\r\n");
        Assert.Equal(30, JoyProLogSettings.Read(folder).RetentionDays);
        File.WriteAllText(Path.Combine(folder, JoyProLog.SettingsFileName), "retention-days=10\r\n");
        Assert.Equal(10, JoyProLogSettings.Read(folder).RetentionDays);
        new JoyProLogSettings { RetentionDays = 7, RetentionChosen = true }.Write(folder);
        JoyProLogSettings chosen = JoyProLogSettings.Read(folder);
        Assert.Equal(7, chosen.RetentionDays);
        Assert.True(chosen.RetentionChosen);
    }

    [Fact]
    public void Start_WritesHeaderAndCorrelatedLinesIntoTheDailyFile()
    {
        var folder = Path.Combine(_temp, "Logs");
        Environment.SetEnvironmentVariable(JoyProLog.SessionVariable, "sess1234");
        Environment.SetEnvironmentVariable(JoyProLog.ParentVariable, "gui-7");

        JoyProLog.Start("test", "1.2.3", ["run", "--game", "DCS"], folder);
        JoyProLog.Info("Unit", "hello from the test");
        JoyProLog.Error("Unit", "it broke", new InvalidOperationException("boom"));
        JoyProLog.Flush();

        Assert.Equal("sess1234", JoyProLog.SessionId);
        Assert.Equal(new Dictionary<string, string> { [JoyProLog.SessionVariable] = "sess1234", [JoyProLog.ParentVariable] = "test-1" },
            JoyProLog.ChildEnvironment(JoyProLog.NewCallId()));
        var file = Path.Combine(folder, JoyProLog.FileName("test", DateTime.Now, 0));
        var text = ReadShared(file);
        Assert.Contains("=== test 1.2.3 started", text);
        Assert.Contains("Command line: run --game DCS", text);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO  test#\d+ s:sess1234 c:gui-7 t:\d+ \[Unit\] hello from the test", text);
        Assert.Contains("ERROR test#", text);
        // The stack trace follows on indented lines.
        Assert.Contains(Environment.NewLine + "    System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void NothingIsWrittenBeforeStartOrBelowTheLevel()
    {
        var folder = Path.Combine(_temp, "Logs");
        JoyProLog.Info("Unit", "before start");
        JoyProLog.Flush();
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder, "*.log").Length > 0);

        Environment.SetEnvironmentVariable(JoyProLog.LevelVariable, "warn");
        JoyProLog.Start("level", "1", null, folder);
        JoyProLog.Info("Unit", "info is filtered");
        JoyProLog.Warn("Unit", "warn is kept");
        JoyProLog.Flush();
        var text = ReadShared(Path.Combine(folder, JoyProLog.FileName("level", DateTime.Now, 0)));
        Assert.DoesNotContain("info is filtered", text);
        Assert.Contains("warn is kept", text);
    }

    [Fact]
    public void Files_RollAtTheSizeCapAndStopAtTheDailyLimit()
    {
        var folder = Path.Combine(_temp, "Logs");
        JoyProLog.MaxFileBytes = 2000;
        JoyProLog.Start("roll", "1", null, folder);
        var line = new string('y', 300);
        for (var i = 0; i < 100; i++)
        {
            JoyProLog.Info("Unit", i.ToString("D3", CultureInfo.InvariantCulture) + " " + line);
            JoyProLog.Flush();
        }

        var files = Directory.GetFiles(folder, "roll-*.log");
        Assert.Equal(JoyProLog.MaxRollsPerDay, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2000, f + " is larger than the cap"));
        Assert.True(File.Exists(Path.Combine(folder, JoyProLog.FileName("roll", DateTime.Now, 1))));
    }

    [Fact]
    public void ShouldLog_IsRateLimitedPerKey()
    {
        JoyProLog.Start("rate", "1", null, Path.Combine(_temp, "Logs"));
        Assert.True(JoyProLog.ShouldLog("tick", TimeSpan.FromMinutes(1)));
        Assert.False(JoyProLog.ShouldLog("tick", TimeSpan.FromMinutes(1)));
        Assert.True(JoyProLog.ShouldLog("other", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Scrubber_RemovesUserFolderUserNamePcAndEmail()
    {
        LogScrubber.Configure(@"C:\Users\jdoe", "jdoe", "GAMINGRIG");
        var text = LogScrubber.Scrub(
            @"Saved Games: C:\Users\jdoe\Saved Games\DCS | json ""C:\\Users\\jdoe\\AppData"" | slash C:/Users/jdoe/Documents | " +
            @"profile jdoe.Arma3Profile | host GAMINGRIG | mail j.doe@example.com | other C:\Users\jdoex\file | JDOE upper");

        Assert.Contains(@"%USERPROFILE%\Saved Games\DCS", text);
        Assert.Contains(@"""%USERPROFILE%\\AppData""", text);
        Assert.Contains("%USERPROFILE%/Documents", text);
        Assert.Contains("<user>.Arma3Profile", text);
        Assert.Contains("host <pc>", text);
        Assert.Contains("mail <email>", text);
        Assert.Contains("<user> upper", text);
        Assert.DoesNotContain("jdoe", text.Replace("jdoex", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", text);
        // A longer name that only starts with the user name stays.
        Assert.Contains(@"C:\Users\jdoex\file", text);
    }

    [Fact]
    public void Scrubber_RedactsSecretsButKeepsOrdinaryWords()
    {
        LogScrubber.Configure(@"C:\Users\someone", "someone", "PC1234");
        var text = LogScrubber.Scrub("password=hunter2 | api_key: abcdefghijklmnop1234 | Authorization: Bearer abc.def.ghijklmnop | " +
                                     "token=0123456789abcdef0123 | marker token: JOY_BTN1 | Basic settings | pass 2 of 3");

        Assert.Contains("password=<redacted>", text);
        Assert.Contains("api_key: <redacted>", text);
        Assert.Contains("Bearer <redacted>", text);
        Assert.Contains("token=<redacted>", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abcdefghijklmnop1234", text);
        Assert.Contains("marker token: JOY_BTN1", text);
        Assert.Contains("Basic settings", text);
        Assert.Contains("pass 2 of 3", text);
    }

    [Fact]
    public void Scrubber_HashesSteamAndGameAccountIds()
    {
        LogScrubber.Configure(@"C:\Users\someone", "someone", "PC1234");
        var text = LogScrubber.Scrub(@"steamid 76561198012345678 | C:\Program Files (x86)\Steam\userdata\11223344\1250410\remote | " +
                                     @"My Games\WarThunder\Saves\7654321\production | Egosoft\X4\87654321\inputmap.xml | DCS 2.9.8.1214");

        Assert.DoesNotContain("76561198012345678", text);
        Assert.DoesNotContain("11223344", text);
        Assert.DoesNotContain("7654321", text);
        Assert.DoesNotContain("87654321", text);
        Assert.Matches(@"steamid steam#[0-9a-f]{8} ", text);
        Assert.Matches(@"userdata\\steam#[0-9a-f]{8}\\1250410", text);
        Assert.Matches(@"Saves\\id#[0-9a-f]{8}\\production", text);
        Assert.Matches(@"X4\\id#[0-9a-f]{8}\\inputmap", text);
        Assert.Contains("DCS 2.9.8.1214", text);
        // Stable within one PC: the same id gives the same hash.
        Assert.Equal(LogScrubber.SteamHash("11223344"), LogScrubber.SteamHash("11223344"));
    }

    [Fact]
    public void LogLines_AreScrubbed()
    {
        LogScrubber.Configure(@"C:\Users\jdoe", "jdoe", "GAMINGRIG");
        var folder = Path.Combine(_temp, "Logs");
        JoyProLog.Start("scrub", "1", null, folder);
        JoyProLog.Info("Unit", @"Exporting to C:\Users\jdoe\Saved Games\DCS for jdoe@example.com");
        JoyProLog.Flush();

        var text = ReadShared(Path.Combine(folder, JoyProLog.FileName("scrub", DateTime.Now, 0)));
        Assert.Contains(@"Exporting to %USERPROFILE%\Saved Games\DCS for <email>", text);
        Assert.DoesNotContain("jdoe", text);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
