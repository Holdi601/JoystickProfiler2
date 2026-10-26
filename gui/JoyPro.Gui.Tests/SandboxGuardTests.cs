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

// The test sandbox (docs/testing.md "Test sandbox"): with JOYPRO_TEST_SANDBOX set the engine refuses every write outside
// the sandbox, temp and its data folder, loudly (exit code 77, stderr, <sandbox>\sandbox-violations.log), and writes
// nothing. The 2.4.3 incident: a test view model's export path defaulted to the real Saved Games\DCS and an export wrote
// into the user's Config\Input.
[SupportedOSPlatform("windows")]
public sealed class SandboxGuardTests
{
    private static async Task<(int Exit, string Output)> Run(string engine, string folder, string sandbox, params string[] args)
    {
        ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data");
        info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
        info.Environment["JOYPRO_TEST_ASSUME_INSTALLED"] = "1";
        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        // Its own sandbox: the refusals of this test do not land in the run's violation log.
        info.Environment["JOYPRO_TEST_SANDBOX"] = sandbox;
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await errors);
    }

    private static Dictionary<string, string> Fingerprint(string folder) =>
        !Directory.Exists(folder)
            ? new Dictionary<string, string>()
            : new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .ToDictionary(f => f.FullName, f => f.Length + "|" + f.LastWriteTimeUtc.Ticks, StringComparer.OrdinalIgnoreCase);

    private static string FixtureSavedGames()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "tests", "fixtures", "dcs-saved-games")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "engine", "tests", "fixtures", "dcs-saved-games");
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    [Fact]
    public async Task ExportOutsideTheSandboxIsRefusedAndWritesNothing()
    {
        string? engine = TestEnvironment.EnginePath();
        Assert.True(engine != null, "engine.exe of this configuration is not built");
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "sandbox-guard-" + Guid.NewGuid().ToString("N"));
        string sandbox = Path.Combine(folder, "sandbox");
        string savedGames = Path.Combine(folder, "sg");
        string profile = Path.Combine(folder, "guard.pr0file");
        // Outside temp, the sandbox and the data folder: a stand-in for a game folder next to the test build.
        string outside = Path.Combine(AppContext.BaseDirectory, "sandbox-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(FixtureSavedGames(), savedGames);
            CopyTree(FixtureSavedGames(), outside);
            (int exit, string output) = await Run(engine!, folder, sandbox, "dcs", "import", savedGames, "--text", "--save-profile", profile);
            Assert.True(exit == 0, output);

            // Inside the sandbox (temp) the export works as always.
            (exit, output) = await Run(engine!, folder, sandbox, "--profile", profile, "dcs", "export", savedGames, "--mode", "clean", "--text");
            Assert.True(exit == 0, output);
            Assert.False(File.Exists(Path.Combine(sandbox, "sandbox-violations.log")), output);

            // Outside: refused before anything is written, loudly.
            Dictionary<string, string> before = Fingerprint(outside);
            (exit, output) = await Run(engine!, folder, sandbox, "--profile", profile, "dcs", "export", outside, "--mode", "clean", "--text");
            Assert.Equal(77, exit);
            Assert.Contains("JOYPRO_TEST_SANDBOX refused", output);
            Assert.Equal(before, Fingerprint(outside));
            Assert.True(File.Exists(Path.Combine(sandbox, "sandbox-violations.log")));
            Assert.Contains(outside, File.ReadAllText(Path.Combine(sandbox, "sandbox-violations.log")), StringComparison.OrdinalIgnoreCase);

            // The real DCS folder of this PC (the incident's path): refused the same way, not a file changes. Only after the
            // stand-in above proved the refusal.
            string realDcs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "DCS");
            if (Directory.Exists(Path.Combine(realDcs, "Config", "Input")))
            {
                Dictionary<string, string> real = Fingerprint(Path.Combine(realDcs, "Config"));
                (exit, output) = await Run(engine!, folder, sandbox, "--profile", profile, "dcs", "export", realDcs, "--text");
                Assert.Equal(77, exit);
                Assert.Contains("JOYPRO_TEST_SANDBOX refused", output);
                Assert.Equal(real, Fingerprint(Path.Combine(realDcs, "Config")));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(outside, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // The app's default game paths in a test are inside the sandbox, never the real Saved Games or Program Files.
    [Fact]
    public void DefaultGamePathsOfATestViewModelAreInTheSandbox()
    {
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        Assert.True(UserFolders.IsSandboxed);
        foreach (string path in new[] { vm.ExportDcsPath, vm.ImportDcsPath, vm.DcsSavedGamesPath, vm.ImportStarCitizenPath, vm.ExportStarCitizenPath })
            Assert.True(string.IsNullOrEmpty(path) || UserFolders.InSandbox(path), "a default game path outside the test sandbox: " + path);
        Assert.True(string.IsNullOrEmpty(vm.DcsInstallPath) || UserFolders.InSandbox(vm.DcsInstallPath), vm.DcsInstallPath);
        Assert.True(UserFolders.InSandbox(ProblemReporter.DefaultFolder));
    }
}
