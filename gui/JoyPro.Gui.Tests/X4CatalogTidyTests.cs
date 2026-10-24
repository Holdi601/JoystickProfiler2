using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// X4 lists built by older versions: the menu section and the builder's notes ("space/Steering: Analog; function 16;
// also in ...") became categories of their own, and the steering axes said neither yaw nor roll (report 2.2.17).
public sealed class X4CatalogTidyTests
{
    [Theory]
    [InlineData("INPUT_RANGE_STEERING_PRIMARY", "Primary Steering Axis", "space/Steering: Analog", "Yaw (Primary Steering Axis)", "Steering: Analog")]
    [InlineData("INPUT_RANGE_STEERING_SECONDARY", "Secondary Steering Axis", "space/Steering: Analog", "Roll (Secondary Steering Axis)", "Steering: Analog")]
    [InlineData("INPUT_RANGE_STEERING_PITCH", "Steer Up-Down", "space/Steering: Analog", "Pitch (Steer Up-Down)", "Steering: Analog")]
    [InlineData("INPUT_STATE_STEER_LEFT", "Steer Left", "space/Steering: Digital", "Yaw Left (Steer Left)", "Steering: Digital")]
    [InlineData("INPUT_ACTION_OPEN_MAP", "Map", "space/Menu Access; function 13", "Map", "Menu Access")]
    [InlineData("INPUT_STATE_INTERACTION_MENU", "Interact", "space/Menu Access; function 3; shared with on-foot Interact; also in firstperson/Walking: Digital, space/Target Management", "Interact", "Menu Access")]
    [InlineData("INPUT_ACTION_DOCK_ACTION", "Dock", "Misc", "Dock", "Misc")]
    public void X4Rows_GetReadableTitlesAndTheMenuGroup(string id, string title, string category, string expectedTitle, string expectedCategory)
    {
        (string t, string c) = MainWindowViewModel.TidyRegistryCatalogRow("X4", id, title, category);
        Assert.Equal(expectedTitle, t);
        Assert.Equal(expectedCategory, c);
    }

    [Fact]
    public void OtherGames_KeepReadableRows()
    {
        (string t, string c) = MainWindowViewModel.TidyRegistryCatalogRow("Arma3", "HeliBrakes", "Wheel brakes", "Helicopter and plane");
        Assert.Equal("Wheel brakes", t);
        Assert.Equal("Helicopter and plane", c);
    }

    // Id-like rows of other games read as words too (the engine's catalog does the same with per-game tables).
    [Theory]
    [InlineData("EliteDangerous", "Pitch Axis", "ship_flight", "Pitch Axis", "Ship flight")]
    [InlineData("MSFS2020", "TOGGLE PUSHBACK", "AIRCRAFT", "Toggle Pushback", "Aircraft")]
    [InlineData("XPlane12", "FMS: key_0", "flight_controls", "FMS: key 0", "Flight controls")]
    public void OtherGames_IdLikeRowsReadAsWords(string game, string title, string category, string expectedTitle, string expectedCategory)
    {
        (string t, string c) = MainWindowViewModel.TidyRegistryCatalogRow(game, "id", title, category);
        Assert.Equal(expectedTitle, t);
        Assert.Equal(expectedCategory, c);
    }
}
