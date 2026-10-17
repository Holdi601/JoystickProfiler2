using System;
using System.IO;
using JoyPro.Gui.Models;
using Xunit;

namespace JoyPro.Gui.Tests;

// A linked installation folder is resolved like the engine does (GameInstalls.ResolveInstallFolder): fake IL-2 Korea
// layouts for Steam (steamapps\common\IL2Series) and the standalone installer (<folder>\Launcher.exe + game\...).
public sealed class GameInstallFolderTests : IDisposable
{
    private static readonly string[] KoreaMarkers = [@"bin\game\IL2Series.exe"];
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-installfolder-" + Guid.NewGuid().ToString("N"));
    private readonly string steam;
    private readonly string app;
    private readonly string game;

    public GameInstallFolderTests()
    {
        steam = Path.Combine(temp, "SteamLibrary", "steamapps", "common", "IL2Series");
        app = Path.Combine(temp, "Program Files", "IL2Series");
        game = Path.Combine(app, "game");
        foreach (string root in new[] { steam, game })
        {
            Directory.CreateDirectory(Path.Combine(root, "bin", "game"));
            Directory.CreateDirectory(Path.Combine(root, "data", "input"));
            File.WriteAllText(Path.Combine(root, "bin", "game", "IL2Series.exe"), "fake");
        }
        File.WriteAllText(Path.Combine(app, "Launcher.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(app, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("steam", "steam")]
    [InlineData(@"steam\bin\game", "steam")]
    [InlineData(@"steam\bin\game\IL2Series.exe", "steam")]
    [InlineData(@"steam\data\input", "steam")]
    [InlineData("app", "game")]
    [InlineData(@"app\Launcher.exe", "game")]
    [InlineData(@"app\cache", "game")]
    [InlineData("game", "game")]
    [InlineData(@"game\bin\game", "game")]
    [InlineData(@"game\bin\game\IL2Series.exe", "game")]
    public void Resolve_FindsTheGameFolderFromWhatThePlayerPicked(string picked, string expected)
    {
        Assert.Equal(Expand(expected), GameInstallFolder.Resolve(KoreaMarkers, Expand(picked)));
    }

    [Fact]
    public void Resolve_KeepsAFolderWithoutMarkerNearby()
    {
        string other = Path.Combine(temp, "Elsewhere");
        Directory.CreateDirectory(other);
        Assert.Equal(other, GameInstallFolder.Resolve(KoreaMarkers, other));
    }

    [Fact]
    public void SetupItem_SaysWhichFolderJoyProUsesForAPickedLauncher()
    {
        var install = new GameInstallItem("IL2Korea", "IL-2 Series: Korea", false, string.Empty, string.Empty, false, false, string.Empty, [], markers: KoreaMarkers);
        var item = new GameSetupItem(install) { LinkedFolder = Path.Combine(app, "Launcher.exe") };

        Assert.True(item.MarkerOk, item.MarkerStatus);
        Assert.Contains(game, item.MarkerStatus);

        item.LinkedFolder = steam;
        Assert.Equal(@"Marker found: bin\game\IL2Series.exe.", item.MarkerStatus);
    }

    private string Expand(string key)
    {
        int slash = key.IndexOf('\\');
        string head = slash < 0 ? key : key[..slash];
        string rest = slash < 0 ? string.Empty : key[(slash + 1)..];
        string root = head switch { "steam" => steam, "app" => app, "game" => game, _ => throw new ArgumentException(key) };
        return rest.Length == 0 ? root : Path.Combine(root, rest);
    }
}
