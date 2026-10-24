using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Gui.Models;
using Xunit;

namespace JoyPro.Gui.Tests;

// One name per game everywhere (report: the Start card said "IL-2 Sturmovik Great Battles", other places "IL-2 Great
// Battles"): DisplayText.Game and the cards use the same full name, the one engine/GameInstalls.cs gives the game.
public sealed class GuiGameNamesTests
{
    [Fact]
    public void EveryGame_HasOneName_TheEnginesOwn()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "engine", "GameInstalls.cs"));
        MatchCollection specs = Regex.Matches(source, "new Spec \\{ GameId = \"([^\"]+)\", Name = \"([^\"]+)\"");
        Assert.True(specs.Count >= 30, "the engine's game list was not found");
        foreach (Match spec in specs)
        {
            string id = spec.Groups[1].Value;
            string engineName = spec.Groups[2].Value;
            Assert.Equal(engineName, DisplayText.Game(id));
            Assert.Equal(engineName, DisplayText.GameName(id, "something else"));
        }
    }

    [Theory]
    [InlineData("IL2Game", "IL-2 Sturmovik: Great Battles")]
    [InlineData("ProjectWingman", "Project Wingman")]
    [InlineData("FalconBMS", "Falcon BMS")]
    [InlineData("X4", "X4: Foundations")]
    public void KnownNames(string id, string name)
    {
        Assert.Equal(name, DisplayText.Game(id));
    }

    [Fact]
    public void UnknownGame_KeepsTheEnginesName()
    {
        Assert.Equal("Some New Game", DisplayText.GameName("SomeNewGame", "Some New Game"));
    }
}
