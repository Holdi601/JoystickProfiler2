using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Report 2.2.17: a game was offered on the Start page (ACE COMBAT) while JoyPro knew none of its actions. engine games
// installs counts them ("Actions"); a game with none that is not installed (nothing to build its list from) leaves the
// Start page, Settings > Games says why; an installed one stays (Update builds its list).
[SupportedOSPlatform("windows")]
public sealed class GamesWithoutActionsTests
{
    [Fact]
    public async Task StartPage_LeavesOutGamesWithoutAnyAction_SettingsSaysWhy()
    {
        Fake fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);

        string[] cards = vm.GameCards.Select(c => c.GameId).OrderBy(g => g, StringComparer.Ordinal).ToArray();
        Assert.Equal(["AceCombat7", "OldEngineGame", "X4"], cards);
        GameInstallItem wingman = vm.OwnedGameInstalls.Single(g => g.GameId == "ProjectWingman");
        Assert.True(wingman.HasNoActions);
        Assert.False(wingman.OfferedOnStartPage);
        Assert.Contains("not found on this PC", wingman.ActionsText);
        GameInstallItem x4 = vm.OwnedGameInstalls.Single(g => g.GameId == "X4");
        Assert.True(x4.HasNoActions && x4.OfferedOnStartPage);
        Assert.Contains("Update", x4.ActionsText);
        Assert.False(vm.OwnedGameInstalls.Single(g => g.GameId == "OldEngineGame").HasNoActions);
        Assert.Equal(string.Empty, vm.OwnedGameInstalls.Single(g => g.GameId == "AceCombat7").ActionsText);

        // Report 2.2.17 "HLL is greyed out": a game that is not found has its buttons off, and the card says why.
        GameCardItem missing = vm.GameCards.Single(c => c.GameId == "AceCombat7");
        Assert.False(missing.CanImport);
        Assert.Contains("did not find ACE COMBAT 7: Skies Unknown", missing.ImportTip);
        Assert.Equal(missing.NotInstalledReason, missing.ExportTip);
        Assert.Contains("Settings > Games", missing.NotInstalledHint);
        GameCardItem found = vm.GameCards.Single(c => c.GameId == "X4");
        Assert.Equal("Take the binds you made in this game into JoyPro", found.ImportTip);
    }

    private sealed class Fake : IEngineCliClient
    {
        // id, installed, actions (null: an engine that does not count).
        private static readonly (string Id, bool Installed, int? Actions)[] Games =
        [
            ("AceCombat7", false, 20), ("ProjectWingman", false, 0), ("X4", true, 0), ("OldEngineGame", false, null)
        ];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });

        public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default) =>
            RunAsync(engineExePath, arguments, cancellationToken);

        private static string Output(IReadOnlyList<string> args)
        {
            if (NoviceGuiTests.Seq(args, "games", "installs"))
            {
                return "{\"Games\":[" + string.Join(",", Games.Select(g =>
                    "{\"GameId\":\"" + g.Id + "\",\"Name\":\"" + g.Id + "\",\"Installed\":" + (g.Installed ? "true" : "false") +
                    ",\"InstallPath\":\"\",\"Source\":\"test\",\"ConfigPaths\":[]" + (g.Actions.HasValue ? ",\"Actions\":" + g.Actions.Value : string.Empty) + "}")) + "]}";
            }
            if (NoviceGuiTests.Seq(args, "games", "integrations"))
            {
                return "{\"Integrations\":[" + string.Join(",", Games.Select(g =>
                    "{\"GameId\":\"" + g.Id + "\",\"DisplayName\":\"" + g.Id + "\",\"Aliases\":[\"" + g.Id.ToLowerInvariant() + "\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}")) + "]}";
            }
            return "{}";
        }
    }
}
