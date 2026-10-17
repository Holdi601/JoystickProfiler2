using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Game covers on the Start page's "Your games" cards (docs/export.md#pictures): every game JoyPro knows gets a picture,
// installed or not. A cover (Steam's on this PC, or downloaded once by the engine) fills the tile, a logo (the
// publisher's mark or the executable's icon) is drawn on JoyPro's badge, and a game without either keeps the badge
// with its own colour and name. The engine is a fake that answers "media" from local files: no test downloads
// anything, and the GUI asks the engine with --no-internet-pictures while the tests block internet pictures.
[SupportedOSPlatform("windows")]
public sealed class GameCoverTests
{
    // Every game of engine media (DCS, IL-2 GB, Star Citizen and the game registry, engine/GameInstalls.cs).
    internal static readonly string[] KnownGames =
    [
        "DCS", "IL2Game", "StarCitizen", "IL2Korea", "MSFS2024", "MSFS2020", "XPlane12", "XPlane11", "WarThunder", "Arma3",
        "Arma2", "Arma2OA", "ArmaReforger", "Wardogs", "HLLVietnam", "Battlefield6", "Battlefield2042", "AceCombat8",
        "AceCombat7", "ProjectWingman", "X4", "EliteDangerous", "FalconBMS", "VTOLVR", "VTOLVRNative", "NuclearOption", "IL21946", "IL2CoD",
        "IL2CoDBlitz", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6"
    ];

    // Not on Steam: the engine's web source is the publisher's mark (prepar3d.com, falcon-bms.com).
    private static readonly HashSet<string> LogoGames = new(StringComparer.OrdinalIgnoreCase) { "FalconBMS", "P3D4", "P3D5", "P3D6" };

    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task GameCards_EveryKnownGameGetsACoverOrABadge_InstalledOrNot()
    {
        // Bitmaps decode through Avalonia's Skia platform.
        AvaloniaRenderThread.Run(() => { });
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "covers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CoverFakeEngine fake = new(folder);
            MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            await vm.RefreshGameCardsCommand.ExecuteAsync(null);

            // The covers load in the background.
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(20) && vm.GameCards.Any(c => c.GameId != CoverFakeEngine.NewGame && c.Cover == null && c.Logo == null))
                await Task.Delay(50);

            Assert.Equal(KnownGames.Length + 1, vm.GameCards.Count);
            foreach (string id in KnownGames)
            {
                GameCardItem card = vm.GameCards.Single(c => c.GameId == id);
                if (LogoGames.Contains(id))
                {
                    Assert.True(card.Cover == null, id + ": a logo must not fill the tile.");
                    Assert.True(card.Logo != null, id + ": no logo on the badge.");
                }
                else
                {
                    Assert.True(card.Cover != null, id + ": no cover.");
                }
                // Every game also has its own badge (colour and short name) under the picture.
                Assert.NotEqual("\u0001", GameBadge.StyleOf(id, "\u0001").Short);
            }
            // Installed or not: a game that is not on this PC keeps its chip and still gets its cover.
            GameCardItem missing = vm.GameCards.Single(c => c.GameId == "P3D6");
            Assert.True(missing.NotInstalled);
            Assert.Equal("Not found on this PC", missing.InstallText);
            GameCardItem fsx = vm.GameCards.Single(c => c.GameId == "FSX");
            Assert.True(fsx.NotInstalled && fsx.Cover != null);

            // A game without any picture keeps JoyPro's badge (its name on a colour of its own).
            GameCardItem newGame = vm.GameCards.Single(c => c.GameId == CoverFakeEngine.NewGame);
            Assert.Null(newGame.Cover);
            Assert.Null(newGame.Logo);
            Assert.Equal("Some New Game", GameBadge.StyleOf(newGame.GameId, newGame.Name).Short);

            // The tests block internet pictures: the engine is told not to download covers.
            IReadOnlyList<string> media = Assert.Single(fake.Calls, c => c.Contains("media"));
            Assert.Contains("--no-internet-pictures", media);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A picture still open: the temp folder is cleaned up later.
            }
        }
    }

    // The setting decides (the global switches are not touched: another test's picture must never be downloaded).
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void MediaCall_AsksForNoDownload_WhenInternetPicturesAreOffOrBlocked(bool enabled, bool blocked, bool flag)
    {
        List<string> arguments = MainWindowViewModel.MediaArguments(enabled, blocked);
        Assert.Equal(["--status", "false", "media"], arguments.Take(3));
        Assert.Equal(flag, arguments.Contains("--no-internet-pictures"));
    }

    // games installs: every known game (Prepar3D v6 and FSX not installed) plus a game without pictures; games
    // integrations: the registry; media: Steam games from the "Steam cache", the others downloaded (logo or cover).
    private sealed class CoverFakeEngine : IEngineCliClient
    {
        public const string NewGame = "SomeNewGame";
        private readonly string folder;

        public CoverFakeEngine(string folder)
        {
            this.folder = folder;
        }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private string Output(IReadOnlyList<string> args)
        {
            IEnumerable<string> all = KnownGames.Append(NewGame);
            if (NoviceGuiTests.Seq(args, "games", "installs"))
            {
                return "{\"Games\":[" + string.Join(",", all.Select(id =>
                    "{\"GameId\":\"" + id + "\",\"Name\":\"" + Name(id) + "\",\"Installed\":" + (id is "P3D6" or "FSX" ? "false" : "true") +
                    ",\"InstallPath\":\"\",\"Source\":\"test\",\"ConfigPaths\":[]}")) + "]}";
            }
            if (NoviceGuiTests.Seq(args, "games", "integrations"))
            {
                return "{\"Integrations\":[" + string.Join(",", all.Where(id => id is not ("DCS" or "IL2Game" or "StarCitizen")).Select(id =>
                    "{\"GameId\":\"" + id + "\",\"DisplayName\":\"" + Name(id) + "\",\"Aliases\":[\"" + id.ToLowerInvariant() + "\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":false}")) + "]}";
            }
            if (args.Contains("media"))
            {
                StringBuilder games = new();
                foreach (string id in KnownGames)
                {
                    bool logo = LogoGames.Contains(id);
                    bool steam = !logo && id is not ("StarCitizen" or "FSX" or "P3D4" or "P3D5" or "P3D6");
                    string file = Path.Combine(folder, id + ".png");
                    File.WriteAllBytes(file, Png);
                    if (games.Length > 0)
                        games.Append(',');
                    games.Append("{\"GameId\":\"").Append(id).Append("\",\"Name\":\"").Append(Name(id)).Append("\",\"Cover\":\"")
                        .Append(file.Replace("\\", "\\\\")).Append("\",\"CoverSource\":\"").Append(steam ? "steam-cache" : "download")
                        .Append("\",\"CoverKind\":\"").Append(logo ? "logo" : "cover").Append("\"}");
                }
                return "{\"Games\":[" + games + "],\"Aircraft\":[],\"Warnings\":[]}";
            }
            return "{}";
        }

        private static string Name(string id) => id == NewGame ? "Some New Game" : id;
    }
}
