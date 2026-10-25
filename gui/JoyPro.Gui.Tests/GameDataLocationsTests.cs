using System;
using System.IO;
using System.Linq;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// The GUI reads the game lists where the engine does (docs/installs.md "Game data"): <data>\GameData first, else the
// engine folder; JoyPro's own DB folders always from the engine folder. Temp folders only (JOYPRO_DATA_DIR).
[Collection("JoyProDataDir")]
public sealed class GameDataLocationsTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-gamedata-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousDataDir = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
    private readonly string engineDir;
    private readonly string gameData;

    public GameDataLocationsTests()
    {
        engineDir = Path.Combine(temp, "engine");
        Directory.CreateDirectory(Path.Combine(engineDir, "DB", "DCS"));
        Directory.CreateDirectory(Path.Combine(engineDir, "DB", "IL2"));
        Directory.CreateDirectory(Path.Combine(engineDir, "DB", "Functions"));
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        gameData = Path.Combine(temp, "data", "GameData");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousDataDir);
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    [Fact]
    public void DbFolder_UsesTheEngineFolderUntilTheGameDataFolderHasTheGame()
    {
        Assert.Equal(Path.Combine(engineDir, "DB", "DCS"), GameDataLocations.DbFolder(engineDir, "DCS"));

        Directory.CreateDirectory(Path.Combine(gameData, "DB", "DCS"));
        Assert.Equal(Path.Combine(gameData, "DB", "DCS"), GameDataLocations.DbFolder(engineDir, "DCS"));
        Assert.Equal(Path.Combine(engineDir, "DB", "IL2"), GameDataLocations.DbFolder(engineDir, "IL2"));
    }

    [Fact]
    public void DbFolder_ReadsJoyProsOwnDataFromTheEngineFolder()
    {
        Directory.CreateDirectory(Path.Combine(gameData, "DB", "Functions"));
        Assert.Equal(Path.Combine(engineDir, "DB", "Functions"), GameDataLocations.DbFolder(engineDir, "Functions"));
    }

    [Fact]
    public void DbFolder_PointsAtTheGameDataFolderForAGameWithoutAList()
    {
        Assert.Equal(Path.Combine(gameData, "DB", "X4"), GameDataLocations.DbFolder(engineDir, "X4"));
    }

    [Fact]
    public void GameDbFolders_ListsEveryGameOnceWithTheGameDataCopyFirst()
    {
        Directory.CreateDirectory(Path.Combine(gameData, "DB", "DCS"));
        Directory.CreateDirectory(Path.Combine(gameData, "DB", "MSFS2024"));

        var folders = GameDataLocations.GameDbFolders(engineDir);

        Assert.Equal(["DCS", "IL2", "MSFS2024"], folders.Select(Path.GetFileName).ToArray());
        Assert.Equal(Path.Combine(gameData, "DB", "DCS"), folders[0]);
        Assert.Equal(Path.Combine(engineDir, "DB", "IL2"), folders[1]);
    }

    // A data folder named by the caller (a view model's GameDataRootOverride) is used instead of JOYPRO_DATA_DIR.
    [Fact]
    public void ExplicitDataRoot_WinsOverJoyproDataDir()
    {
        Directory.CreateDirectory(Path.Combine(gameData, "DB", "DCS"));
        string own = Path.Combine(temp, "own-data");

        Assert.Equal(Path.Combine(engineDir, "DB", "DCS"), GameDataLocations.DbFolder(engineDir, "DCS", own));
        Assert.Equal(["DCS", "IL2"], GameDataLocations.GameDbFolders(engineDir, own).Select(Path.GetFileName).ToArray());
        Assert.Equal(Path.Combine(engineDir, "DB", "DCS"), GameDataLocations.GameDbFolders(engineDir, own)[0]);

        Directory.CreateDirectory(Path.Combine(own, "GameData", "DB", "DCS"));
        Assert.Equal(Path.Combine(own, "GameData", "DB", "DCS"), GameDataLocations.DbFolder(engineDir, "DCS", own));
        Assert.Equal(Path.Combine(gameData, "DB", "DCS"), GameDataLocations.DbFolder(engineDir, "DCS", string.Empty));
    }
}
