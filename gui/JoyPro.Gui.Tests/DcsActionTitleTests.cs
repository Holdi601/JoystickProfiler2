using System;
using System.Collections.Generic;
using System.IO;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Action titles of DCS DB files: a title stored as the raw lua key ("Input.SpitfireLFMkIX.flaps_1") takes the title of
// the same id from the aircraft's sibling file, else a readable form of the key.
public sealed class DcsActionTitleTests
{
    private static string Row(string title, string category, string id) =>
        "<tr>\n<td></td>\n<td>" + title + "</td>\n<td>" + category + "</td>\n<td>" + id + "</td>\n</tr>\n";

    [Fact]
    public void RawLuaKeyTitle_UsesSiblingTitle_ElseReadableKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dcs-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "SpitfireLFMkIX.html"), "<table>" +
                Row("Input.SpitfireLFMkIX.flaps_1", "Flight Control", "d3009pnilunilcd1vd1vpnilvunil") +
                Row("Input.SpitfireLFMkIX.fuel_cock_droptank", "Fuel", "d3010pnilunilcd1vd1vpnilvunil") +
                Row("Gear up", "Flight Control", "d3011pnilunilcd1vd1vpnilvunil") + "</table>");
            File.WriteAllText(Path.Combine(dir, "SpitfireLFMkIX_easy.html"), "<table>" +
                Row("Flaps EXTEND", "Flight Control", "d3009pnilunilcd1vd1vpnilvunil") + "</table>");
            File.WriteAllText(Path.Combine(dir, "AV8BNA.html"), "<table>" + Row("Flaps STOL", "VSTOL Controls", "d3454pnilunilcd13vd0vpnilvunil") + "</table>");

            Dictionary<string, string> spitfire = MainWindowViewModel.LoadDcsTitleFile(Path.Combine(dir, "SpitfireLFMkIX.html"));
            Assert.Equal("Flaps EXTEND", spitfire["d3009pnilunilcd1vd1vpnilvunil"]);
            Assert.Equal("Fuel cock droptank", spitfire["d3010pnilunilcd1vd1vpnilvunil"]);
            Assert.Equal("Gear up", spitfire["d3011pnilunilcd1vd1vpnilvunil"]);

            Dictionary<string, string> harrier = MainWindowViewModel.LoadDcsTitleFile(Path.Combine(dir, "AV8BNA.html"));
            Assert.Equal("Flaps STOL", harrier["d3454pnilunilcd13vd0vpnilvunil"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadableKey_And_RawTitleDetection()
    {
        Assert.Equal("Flaps 1", MainWindowViewModel.ReadableDcsKey("Input.SpitfireLFMkIX.flaps_1"));
        Assert.Equal("Flaps STOL", MainWindowViewModel.ReadableDcsKey("Flaps STOL"));
        Assert.True(MainWindowViewModel.IsRawDcsTitle("Input.SpitfireLFMkIX.flaps_1", "d1"));
        Assert.True(MainWindowViewModel.IsRawDcsTitle("d1", "d1"));
        Assert.False(MainWindowViewModel.IsRawDcsTitle("Flaps STOL", "d1"));
    }

    // Search terms match id, title, description (where the raw key stays) and aircraft: "flaps", "flaps_1", "spitfire".
    [Fact]
    public void Search_MatchesIdTitleRawKeyAndAircraft()
    {
        foreach (string term in new[] { "flaps", "flaps_1", "spitfire", "d3009p" })
            Assert.True(MainWindowViewModel.NodeTextMatches("d3009pnilunilcd1vd1vpnilvunil", "Flaps EXTEND", "Input.SpitfireLFMkIX.flaps_1", "SpitfireLFMkIX", term), term);
        foreach (string term in new[] { "flaps", "stol", "av8b" })
            Assert.True(MainWindowViewModel.NodeTextMatches("d3454pnilunilcd13vd0vpnilvunil", "Flaps STOL", "", "AV8BNA", term), term);
        Assert.False(MainWindowViewModel.NodeTextMatches("d1", "Gear up", "", "AV8BNA", "flaps"));
    }

    // The real DB of this machine (read only), when present: both ids of the tester's relation "FLAP Switch - FULL" resolve.
    [Fact]
    public void RealDcsDb_ResolvesTheReportedIds_WhenPresent()
    {
        string dir = Path.Combine(JoyPro.Gui.Tests.QualityGate.Gate.UserGameData(), "DB", "DCS");
        string spitfire = Path.Combine(dir, "SpitfireLFMkIX.html");
        string harrier = Path.Combine(dir, "AV8BNA.html");
        if (!File.Exists(spitfire) || !File.Exists(harrier))
            return;

        Assert.Equal("Flaps EXTEND", MainWindowViewModel.LoadDcsTitleFile(spitfire)["d3009pnilunilcd1vd1vpnilvunil"]);
        Assert.Equal("Flaps STOL", MainWindowViewModel.LoadDcsTitleFile(harrier)["d3454pnilunilcd13vd0vpnilvunil"]);
    }
}
