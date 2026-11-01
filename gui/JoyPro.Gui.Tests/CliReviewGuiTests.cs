using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The GUI side of the CLI review of 2026-11-01: War Thunder's archive switch reaches every command that may build the
// command list, and Import Relations / Import Binds JSON ask before they replace everything (engine ... import --replace).
[SupportedOSPlatform("windows")]
public sealed class CliReviewGuiTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "joypro-cli-review-" + Guid.NewGuid().ToString("N"));

    public CliReviewGuiTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("warthunder", "scan", true)]
    [InlineData("warthunder", "import", true)]
    [InlineData("wt", "export", true)]
    [InlineData("warthunder", "refresh-db", true)]
    [InlineData("warthunder", "paths", false)]
    [InlineData("dcs", "import", false)]
    [InlineData("msfs2024", "refresh-db", false)]
    public void ArchiveSwitch_GoesToEveryWarThunderCommandThatMayBuildTheList(string game, string action, bool expected)
    {
        Assert.Equal(expected, MainWindowViewModel.NeedsNoGameArchivesFlag([game, action], readArchives: false));
        // Archive reading on (the default): never.
        Assert.False(MainWindowViewModel.NeedsNoGameArchivesFlag([game, action], readArchives: true));
        // Already there: not twice.
        Assert.False(MainWindowViewModel.NeedsNoGameArchivesFlag([game, action, "--no-game-archives"], readArchives: false));
    }

    [Fact]
    public async Task ArchiveReadingOff_FirstScanOfWarThunder_PassesTheSwitch()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.ReadWarThunderArchives = false;
        await vm.RunEngineForTestsAsync(["warthunder", "scan"]);
        Assert.Contains("--no-game-archives", fake.Calls.Single(c => c.Contains("warthunder")));

        fake.Calls.Clear();
        vm.ReadWarThunderArchives = true;
        await vm.RunEngineForTestsAsync(["warthunder", "scan"]);
        Assert.DoesNotContain("--no-game-archives", fake.Calls.Single(c => c.Contains("warthunder")));
    }

    [Fact]
    public async Task ImportRelationsJson_AsksFirst_AndReplacesOnlyOnYes()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(folder, "profile.pr0file");
        vm.RelationImportPath = Path.Combine(folder, "relations.json");
        List<ConfirmRequest> asked = [];

        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(false); };
        await vm.ImportRelationsCommand.ExecuteAsync(null);
        ConfirmRequest question = Assert.Single(asked);
        Assert.True(question.IsDanger);
        Assert.Contains("replace all relations and binds", question.Message);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("relations") && c.Contains("import"));

        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(true); };
        await vm.ImportRelationsCommand.ExecuteAsync(null);
        string[] import = fake.Calls.Single(c => c.Contains("relations") && c.Contains("import"));
        Assert.Contains("--replace", import);
    }

    [Fact]
    public async Task ImportBindsJson_AsksFirst_AndReplacesOnlyOnYes()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(folder, "profile.pr0file");
        vm.BindsImportPath = Path.Combine(folder, "binds.json");

        vm.ConfirmHandler = _ => Task.FromResult(false);
        await vm.ImportBindsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("binds") && c.Contains("import"));

        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(true); };
        await vm.ImportBindsCommand.ExecuteAsync(null);
        Assert.Contains("replace all binds", Assert.Single(asked).Message);
        Assert.Contains("--replace", fake.Calls.Single(c => c.Contains("binds") && c.Contains("import")));
    }

    private static MainWindowViewModel CreateVm(RecordingEngine fake) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };

    private sealed class RecordingEngine : IEngineCliClient
    {
        public List<string[]> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = "{}",
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }
    }
}
