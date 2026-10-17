using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The updater (docs/updates.md) end to end against local fake release feeds (file:// and a localhost HTTP server):
// no release, the same version, a newer one (download, checksum, unpack, the copy script on a temp install), a bad
// checksum, no internet, GitHub's rate limit; and the dialog of the view model (at start / manual, Update now, Later,
// Skip this version). Nothing is published and nothing outside the temp folder is touched.
[SupportedOSPlatform("windows")]
public sealed class UpdateFeedTests : IDisposable
{
    private static readonly SemVersion Current = new(2, 0, 0, string.Empty);
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-update-" + Guid.NewGuid().ToString("N"));

    public UpdateFeedTests() => Directory.CreateDirectory(temp);

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private UpdateService Service(string feed) => new(feed, Current, updatesFolder: Path.Combine(temp, "Updates"));

    [Theory]
    [InlineData("v2.1.0", 2, 1, 0, "")]
    [InlineData("2.0.3-beta.1", 2, 0, 3, "beta.1")]
    [InlineData("V10.4", 10, 4, 0, "")]
    [InlineData("2.1.0+abc1234", 2, 1, 0, "")]
    public void SemVersion_ReadsReleaseTags(string tag, int major, int minor, int patch, string pre)
    {
        Assert.Equal(new SemVersion(major, minor, patch, pre), SemVersion.Parse(tag));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("v2.x")]
    [InlineData("1..2")]
    public void SemVersion_RejectsNonVersions(string tag) => Assert.Null(SemVersion.Parse(tag));

    [Theory]
    [InlineData("2.1.0", "2.0.9", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("2.1.0", "2.1.0-beta.2", 1)]
    [InlineData("2.1.0-beta.2", "2.1.0-beta.10", -1)]
    [InlineData("2.1.0-alpha", "2.1.0-beta", -1)]
    [InlineData("2.1.0-rc.1", "2.1.0-rc", 1)]
    [InlineData("10.0.0", "9.9.9", 1)]
    public void SemVersion_ComparesLikeSemanticVersioning(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVersion.Parse(a)!.Value.CompareTo(SemVersion.Parse(b)!.Value)));
    }

    [Fact]
    public void Repository_IsReadFromWhatTheUserTyped()
    {
        Assert.Equal("Holdi601/JoystickProfiler2", UpdateService.NormalizeRepository(""));
        Assert.Equal("someone/JoyFork", UpdateService.NormalizeRepository("https://github.com/someone/JoyFork.git"));
        Assert.Equal("someone/JoyFork", UpdateService.NormalizeRepository(" someone/JoyFork/ "));
        Assert.Equal("https://github.com/Holdi601/JoystickProfiler2/releases", UpdateService.ReleasesPageFor(null));
        string? feed = Environment.GetEnvironmentVariable(UpdateService.FeedVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateService.FeedVariable, null);
            Assert.Equal("https://api.github.com/repos/someone/JoyFork/releases/latest", UpdateService.FeedUrlFor("someone/JoyFork"));
            Environment.SetEnvironmentVariable(UpdateService.FeedVariable, @"C:\feeds\latest.json");
            Assert.Equal(@"C:\feeds\latest.json", UpdateService.FeedUrlFor("someone/JoyFork"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateService.FeedVariable, feed);
        }
    }

    [Fact]
    public async Task Check_NoRelease_SameVersion_Newer_NoPackage_Broken_Offline()
    {
        // No release: the feed does not exist (GitHub answers 404 for a repository without releases or a private one).
        UpdateCheckResult none = await Service(Path.Combine(temp, "nothing.json")).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.NoRelease, none.Outcome);
        Assert.Null(none.Release);

        FakeReleaseFeed same = FakeReleaseFeed.Create(Path.Combine(temp, "same"), "2.0.0");
        UpdateCheckResult upToDate = await Service(same.FeedPath).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.UpToDate, upToDate.Outcome);
        Assert.False(upToDate.IsNewer);
        Assert.Contains("up to date", upToDate.Message);

        FakeReleaseFeed older = FakeReleaseFeed.Create(Path.Combine(temp, "older"), "1.9.5");
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Service(older.FeedPath).CheckAsync()).Outcome);

        FakeReleaseFeed newer = FakeReleaseFeed.Create(Path.Combine(temp, "newer"), "2.1.0");
        UpdateCheckResult offer = await Service(new Uri(newer.FeedPath).AbsoluteUri).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.NewerAvailable, offer.Outcome);
        Assert.Equal(new SemVersion(2, 1, 0, string.Empty), offer.Release!.Version);
        Assert.Equal("v2.1.0", offer.Release.Tag);
        Assert.Contains("Hidden games on the Start page", offer.Release.Notes);
        Assert.EndsWith("JoyPro2-2.1.0-win-x64.zip", offer.Release.PackageUrl);
        Assert.True(offer.Release.PackageSize > 0);
        Assert.Equal("https://github.com/Holdi601/JoystickProfiler2/releases/tag/v2.1.0", offer.Release.PageUrl);

        FakeReleaseFeed bare = FakeReleaseFeed.Create(Path.Combine(temp, "bare"), "2.2.0", withPackage: false);
        UpdateCheckResult noPackage = await Service(bare.FeedPath).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.NewerWithoutPackage, noPackage.Outcome);
        Assert.True(noPackage.IsNewer);

        string broken = Path.Combine(temp, "broken.json");
        File.WriteAllText(broken, "<html>not json</html>");
        Assert.Equal(UpdateCheckOutcome.Failed, (await Service(broken).CheckAsync()).Outcome);
        string noTag = Path.Combine(temp, "notag.json");
        File.WriteAllText(noTag, "{\"tag_name\":\"latest\",\"assets\":[]}");
        Assert.Equal(UpdateCheckOutcome.Failed, (await Service(noTag).CheckAsync()).Outcome);

        // No internet: nothing listens.
        UpdateCheckResult offline = await Service(FakeReleaseFeed.UnreachableFeed()).CheckAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, offline.Outcome);
        Assert.Contains("Could not reach", offline.Message);
    }

    [Fact]
    public async Task Http_Newer_DownloadsVerifiesAndUnpacks_RateLimitAndMissingAreReported()
    {
        string folder = Path.Combine(temp, "http");
        using LocalReleaseServer server = new(folder);
        FakeReleaseFeed feed = FakeReleaseFeed.Create(folder, "2.1.0", urlBase: server.BaseUrl);
        UpdateService service = Service(server.BaseUrl + Path.GetFileName(feed.FeedPath));

        UpdateCheckResult result = await service.CheckAsync();
        Assert.Equal(UpdateCheckOutcome.NewerAvailable, result.Outcome);
        Assert.StartsWith(server.BaseUrl, result.Release!.PackageUrl);

        List<string> steps = [];
        List<double> progress = [];
        string staged = await service.StageAsync(result.Release, new SyncProgress<string>(steps.Add), new SyncProgress<double>(progress.Add));
        Assert.True(File.Exists(Path.Combine(staged, "JoyPro.Gui.exe")));
        Assert.Equal("fake JoyPro 2.1.0", File.ReadAllText(Path.Combine(staged, "JoyPro.Gui.exe")));
        Assert.True(File.Exists(Path.Combine(staged, "engine", "DB", "Functions", "functions.txt")));
        Assert.StartsWith(Path.Combine(temp, "Updates", "2.1.0"), staged);
        Assert.Equal(["Downloading JoyPro2 2.1.0...", "Checking the download...", "Unpacking..."], steps);
        Assert.Equal(1, progress.Last());
        Assert.False(File.Exists(Path.Combine(temp, "Updates", "2.1.0", "package.zip")));

        Assert.Equal(UpdateCheckOutcome.Failed, (await Service(server.BaseUrl + "ratelimit").CheckAsync()).Outcome);
        Assert.Contains("too many checks", (await Service(server.BaseUrl + "ratelimit").CheckAsync()).Message);
        Assert.Equal(UpdateCheckOutcome.NoRelease, (await Service(server.BaseUrl + "missing").CheckAsync()).Outcome);
    }

    [Fact]
    public async Task BadChecksum_IsRefused_AndNothingIsUnpacked()
    {
        FakeReleaseFeed feed = FakeReleaseFeed.Create(Path.Combine(temp, "bad"), "2.1.0", badHash: true);
        UpdateService service = Service(feed.FeedPath);
        UpdateCheckResult result = await service.CheckAsync();
        Assert.Equal(UpdateCheckOutcome.NewerAvailable, result.Outcome);
        UpdateVerificationException error = await Assert.ThrowsAsync<UpdateVerificationException>(() => service.StageAsync(result.Release!, null));
        Assert.Contains("does not match its checksum", error.Message);
        Assert.False(Directory.Exists(Path.Combine(temp, "Updates", "2.1.0")));

        // A checksum file that holds no checksum at all.
        File.WriteAllText(feed.ChecksumPath, "404: Not Found");
        UpdateVerificationException unreadable = await Assert.ThrowsAsync<UpdateVerificationException>(() => service.StageAsync(result.Release!, null));
        Assert.Contains("could not be read", unreadable.Message);
    }

    [Fact]
    public void ReadChecksum_TakesTheFirstWord()
    {
        string hash = new('a', 64);
        Assert.Equal(hash, UpdateService.ReadChecksum(hash + "  JoyPro2-2.1.0-win-x64.zip"));
        Assert.Equal(hash, UpdateService.ReadChecksum("\n" + hash.ToUpperInvariant().ToLowerInvariant() + "\r\n"));
        Assert.Equal(string.Empty, UpdateService.ReadChecksum("not a hash"));
        Assert.Equal(string.Empty, UpdateService.ReadChecksum(""));
    }

    // The copy script for real, on a temp "install": program files replaced, a DB file the user refreshed (newer) kept,
    // an older one replaced, a log written, no restart asked.
    [Fact]
    public async Task InstallerScript_CopiesOverTheInstall_KeepsNewerDbFiles_AndLogs()
    {
        FakeReleaseFeed feed = FakeReleaseFeed.Create(Path.Combine(temp, "install-feed"), "2.1.0");
        UpdateService service = Service(feed.FeedPath);
        UpdateCheckResult result = await service.CheckAsync();
        string staged = await service.StageAsync(result.Release!, null);
        File.WriteAllText(Path.Combine(staged, "engine", "DB", "Functions", "shipped.txt"), "new shipped list");

        string install = Path.Combine(temp, "install");
        Directory.CreateDirectory(Path.Combine(install, "engine", "DB", "Functions"));
        File.WriteAllText(Path.Combine(install, "JoyPro.Gui.exe"), "old JoyPro");
        File.WriteAllText(Path.Combine(install, "user-notes.txt"), "stays");
        string refreshed = Path.Combine(install, "engine", "DB", "Functions", "functions.txt");
        File.WriteAllText(refreshed, "refreshed by the user");
        File.SetLastWriteTimeUtc(refreshed, DateTime.UtcNow.AddDays(1));
        string old = Path.Combine(install, "engine", "DB", "Functions", "shipped.txt");
        File.WriteAllText(old, "old shipped list");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        string script = UpdateService.WriteInstallerScript(staged);
        string log = Path.Combine(temp, "update.log");
        ProcessStartInfo start = UpdateService.BuildInstallerStart(script, staged, install, 999999, string.Empty, log);
        Assert.Equal("powershell.exe", start.FileName);
        Assert.Contains("-ProcessId 999999", start.Arguments);
        Assert.Contains("-Target \"" + install + "\"", start.Arguments);

        // Run it hidden and wait (the test's own start, not the shell start JoyPro uses).
        ProcessStartInfo run = new("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProcessId", "999999", "-Source", staged, "-Target", install, "-Restart", string.Empty, "-Log", log })
            run.ArgumentList.Add(arg);
        using (Process process = Process.Start(run)!)
        {
            Assert.True(process.WaitForExit(120000), "the copy script did not finish");
        }

        Assert.Equal("fake JoyPro 2.1.0", File.ReadAllText(Path.Combine(install, "JoyPro.Gui.exe")));
        Assert.Equal("fake engine 2.1.0", File.ReadAllText(Path.Combine(install, "engine", "engine.exe")));
        Assert.Equal("stays", File.ReadAllText(Path.Combine(install, "user-notes.txt")));
        Assert.Equal("refreshed by the user", File.ReadAllText(refreshed));
        Assert.Equal("new shipped list", File.ReadAllText(old));
        string logText = File.ReadAllText(log);
        Assert.Contains("Update installed from", logText);
        Assert.Contains("No restart asked.", logText);
        Assert.DoesNotContain("Update failed", logText);
    }

    // ---- The view model's dialog ----

    private (MainWindowViewModel Vm, List<string> Installed, Func<int> Shutdowns) CreateVm(Func<string> feed)
    {
        MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        List<string> installed = [];
        int shutdowns = 0;
        vm.UpdateServiceFactory = () => Service(feed());
        vm.UpdateInstallerOverride = installed.Add;
        vm.ShutdownRequested += () => shutdowns++;
        return (vm, installed, () => shutdowns);
    }

    [Fact]
    public async Task AtStart_OnlyANewerNotSkippedVersionAsks()
    {
        string feedPath = Path.Combine(temp, "nothing.json");
        (MainWindowViewModel vm, _, _) = CreateVm(() => feedPath);

        // Nothing / up to date / offline at start: no dialog, only the status in Settings.
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.False(vm.UpdatePromptVisible);
        Assert.Contains("No published release", vm.UpdateStatus);
        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "same"), "2.0.0").FeedPath;
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.False(vm.UpdatePromptVisible);
        feedPath = FakeReleaseFeed.UnreachableFeed();
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.False(vm.UpdatePromptVisible);
        Assert.Equal("v" + AppInfo.Version, vm.VersionCornerText);

        // Newer: asks, with what changed; the corner names it.
        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "newer"), "2.1.0").FeedPath;
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.True(vm.UpdatePromptVisible);
        Assert.True(vm.UpdatePromptOffersUpdate);
        Assert.Equal("JoyPro2 2.1.0 is available", vm.UpdatePromptTitle);
        Assert.Contains("Hidden games on the Start page", vm.UpdatePromptNotes);
        Assert.True(vm.UpdateAvailable);
        Assert.Contains("update 2.1.0", vm.VersionCornerText);

        // Later: closes; the next start asks again.
        vm.UpdateLaterCommand.Execute(null);
        Assert.False(vm.UpdatePromptVisible);
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.True(vm.UpdatePromptVisible);

        // Skip this version: not asked at start any more and not in the corner; a manual check still shows it.
        vm.SkipUpdateVersionCommand.Execute(null);
        Assert.Equal("v2.1.0", vm.SkippedUpdateVersion);
        Assert.False(vm.UpdatePromptVisible);
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.False(vm.UpdatePromptVisible);
        Assert.Equal("v" + AppInfo.Version, vm.VersionCornerText);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatePromptVisible);
        Assert.Contains("skipped this version before", vm.UpdatePromptMessage);

        // A version after the skipped one asks again at start.
        vm.UpdateLaterCommand.Execute(null);
        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "newer2"), "2.2.0").FeedPath;
        await vm.CheckForUpdatesCoreAsync(manual: false);
        Assert.True(vm.UpdatePromptVisible);
        Assert.Equal("JoyPro2 2.2.0 is available", vm.UpdatePromptTitle);
    }

    [Fact]
    public async Task ManualCheck_SaysWhatItFound()
    {
        string feedPath = Path.Combine(temp, "nothing.json");
        (MainWindowViewModel vm, _, _) = CreateVm(() => feedPath);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatePromptVisible);
        Assert.Equal("No release found", vm.UpdatePromptTitle);
        Assert.True(vm.UpdatePromptIsInfo);
        vm.CloseUpdatePromptCommand.Execute(null);
        Assert.False(vm.UpdatePromptVisible);

        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "same"), "2.0.0").FeedPath;
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("JoyPro2 is up to date", vm.UpdatePromptTitle);
        Assert.True(vm.UpdatePromptIsOk);
        Assert.False(vm.UpdatePromptOffersUpdate);

        feedPath = FakeReleaseFeed.UnreachableFeed();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("Could not check for updates", vm.UpdatePromptTitle);
        Assert.True(vm.UpdatePromptIsError);
        Assert.Contains("no internet", vm.UpdatePromptMessage);

        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "bare"), "2.1.0", withPackage: false).FeedPath;
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatePromptIsWarn);
        Assert.False(vm.UpdatePromptOffersUpdate);
        Assert.True(vm.UpdatePromptCanOpenPage);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task UpdateNow_BadChecksumFails_AGoodPackageIsInstalledAndJoyProCloses()
    {
        string feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "bad"), "2.1.0", badHash: true).FeedPath;
        (MainWindowViewModel vm, List<string> installed, Func<int> shutdowns) = CreateVm(() => feedPath);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatePromptOffersUpdate);
        await vm.UpdateNowCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatePromptVisible);
        Assert.True(vm.UpdatePromptIsError);
        Assert.Equal("The update did not work", vm.UpdatePromptTitle);
        Assert.Contains("does not match its checksum", vm.UpdatePromptMessage);
        Assert.Contains("stays as it is", vm.UpdatePromptMessage);
        Assert.Empty(installed);
        Assert.Equal(0, shutdowns());

        feedPath = FakeReleaseFeed.Create(Path.Combine(temp, "good"), "2.1.0").FeedPath;
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        await vm.UpdateNowCommand.ExecuteAsync(null);
        string staged = Assert.Single(installed);
        Assert.True(File.Exists(Path.Combine(staged, "JoyPro.Gui.exe")));
        Assert.Equal(1, shutdowns());
        Assert.Equal("Installing JoyPro2 2.1.0", vm.UpdatePromptTitle);
        Assert.Equal(100, vm.UpdatePromptProgress);
        Assert.Contains("restarts", vm.UpdateStatus);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
