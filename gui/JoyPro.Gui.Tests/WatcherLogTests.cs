using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Logging;
using Xunit;

namespace JoyPro.Gui.Tests;

// What the tray host decides after a game closed is written to its background log (tray-<date>.log, docs/logging.md):
// a problem report shows why a notification came or did not come, and the live watchdog test
// (tools\e2e\games\Watchdog.ps1) reads these lines. The log is process-wide, so this class runs with the tray tests.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class WatcherLogTests : IDisposable
{
    private const string DcsKey = "DCS|C:\\Saved Games\\DCS";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "joypro-watcher-log-" + Guid.NewGuid().ToString("N"));

    public WatcherLogTests()
    {
        JoyProLog.Stop();
        JoyProLog.Start("tray", "1.0", null, folder);
    }

    public void Dispose()
    {
        JoyProLog.Stop();
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task TrayHost_LogsNotification_SilentAccept_UnchangedContent_AndThePause()
    {
        TrayHostTests.Rig rig = new();
        await rig.Host.StartAsync();

        rig.Watchers[0].Raise(DcsKey);
        rig.Engine.DiffChanges = 0;
        rig.Watchers[0].Raise(DcsKey);
        rig.Engine.GameState = "unchanged";
        rig.Watchers[0].Raise(DcsKey);
        rig.Gui.Running = true;
        rig.Gui.RaiseStarted();

        string log = ReadLog();
        Assert.Contains("[Watcher] " + DcsKey + ": 2 change(s) JoyPro manages; notification shown", log);
        Assert.Contains("[Watcher] " + DcsKey + ": the config changed, but nothing JoyPro manages; accepted without asking", log);
        Assert.Contains("[Watcher] " + DcsKey + ": the config was written, its content is unchanged; nothing to ask", log);
        Assert.Contains("[Tray] JoyPro2 started: it watches the games itself, the tray host pauses", log);
        Assert.Single(rig.View.Notes);
    }

    private string ReadLog()
    {
        JoyProLog.Flush();
        return string.Join(Environment.NewLine, Directory.GetFiles(folder, "tray-*.log").Select(File.ReadAllText));
    }
}
