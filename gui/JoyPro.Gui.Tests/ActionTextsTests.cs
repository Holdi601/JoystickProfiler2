using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using JoyPro.Engine;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// JoyPro's own action names and explanations (engine\ActionTexts.cs, DB\Functions\ActionTexts\<game>.tsv) and the check
// for unexplained abbreviations in titles (docs/relation-builder.md "Readable names").
public sealed class ActionTextsTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ShippedFolder() => ActionTexts.FolderUnder(Path.Combine(RepoRoot(), "EngineOnly_Runtime", "DB", "Functions"));

    [Theory]
    [InlineData("rb_pitch", "inprb_pitch", true)]
    [InlineData("rb_pitch", "Rb pitch", true)]
    [InlineData("rb_pitch", "RB pitch", true)]
    [InlineData("cam_switch_mc_far", "Camera switch MC far", true)]
    [InlineData("ext_action1", "Extra action 1", true)]
    [InlineData("ext_action1", "Ext action1", true)]
    [InlineData("pilot.site_dist_plus", "Site dist plus", true)]
    [InlineData("rpc_pitch", "", true)]
    [InlineData("rpc_pitch", "Plane control: pitch", false)]
    [InlineData("gear", "Gear up/down", false)]
    public void WeakTitles_AreOnlyTheIdOrAStringTableKey(string id, string title, bool weak)
    {
        Assert.Equal(weak, ActionTexts.IsWeakTitle(id, title));
    }

    [Theory]
    [InlineData("IL2Korea", "rb_pitch", "Rb pitch", "Rb")]
    [InlineData("IL2Korea", "mc_yaw", "Mc yaw", "Mc")]
    [InlineData("IL2Game", "rb_pitch", "RB pitch", "RB")]
    [InlineData("IL2Korea", "rpc_pitch", "Rpc pitch", "Rpc")]
    [InlineData("IL2CoD", "turret.trt_up", "Trt up", "Trt")]
    [InlineData("XPlane12", "sim/radios/power_adf1_dn", "Power ADF1 dn", "dn")]
    [InlineData("IL2Game", "rpc_pitch", "Plane control: pitch", null)]
    [InlineData("FalconBMS", "SimHUDOn", "HUD: Power - On", null)]
    [InlineData("IL2Game", "x", "Engines outlet cowl shutters (A-20, Fw 190 A-5, Ju 88, P-40)", null)]
    [InlineData("XPlane12", "sim/radios/com1_fine", "COM 1 fine down (25kHz)", null)]
    [InlineData("IL2Korea", "switchgunpos1", "Switch to 1st firing point", null)]
    [InlineData("XPlane12", "sim/fuel/re-fuel_half", "Re-fuel the plane to half tanks!", null)]
    [InlineData("DCS", "d3015", "CMDS Switch - Fwd/Center", null)]
    public void UnexplainedAbbreviation_FindsIdShorthandOnly(string game, string id, string title, string? expected)
    {
        Assert.Equal(expected, ActionTexts.UnexplainedAbbreviation(game, id, title));
    }

    [Theory]
    [InlineData("FalconBMS", "HUD: DED Data Switch - Step Dn", "HUD: DED Data Switch - Step Down")]
    [InlineData("XPlane12", "GNS NAV 1 page dn", "GNS NAV 1 page down")]
    [InlineData("MSFS2020", "Rotor Gov Switch On", "Rotor Governor Switch On")]
    [InlineData("MSFS2020", "Mac Cready Setting Increase", "MacCready Setting Increase")]
    [InlineData("StarCitizen", "Ui back", "UI back")]
    [InlineData("XPlane12", "Fms: Index", "FMS: Index")]
    [InlineData("FalconBMS", "CKPIT: Toggle Mouse Btns in 3D", "CKPIT: Toggle Mouse Buttons in 3D")]
    [InlineData("XPlane12", "Re-fuel the plane", "Re-fuel the plane")]
    [InlineData("DCS", "CMS Switch - Dn", "CMS Switch - Dn")]
    public void ExpandAbbreviations_WritesShorthandOut_NotForDcs(string game, string title, string expected)
    {
        Assert.Equal(expected, ActionTexts.ExpandAbbreviations(game, title));
    }

    [Fact]
    public void WordPlusAxis_IsFlagged()
    {
        Assert.True(ActionTexts.IsWordPlusAxis("Cursor X"));
        Assert.True(ActionTexts.IsWordPlusAxis("Turret RY axis"));
        Assert.False(ActionTexts.IsWordPlusAxis("Radar cursor: left/right"));
    }

    [Fact]
    public void BestTitle_GameTextWinsExceptForKeysAndKorea()
    {
        string folder = ShippedFolder();
        ActionTexts.Reset();
        // IL-2 GB: the game's own text stays, a string-table key becomes JoyPro's name.
        Assert.Equal("Plane control: pitch", ActionTexts.BestTitle(folder, "IL2Game", "rpc_pitch", "Plane control: pitch"));
        Assert.Equal("Mouse turret aim: up/down", ActionTexts.BestTitle(folder, "IL2Game", "rb_pitch", "inprb_pitch"));
        Assert.Equal("Mouse control: direction up/down", ActionTexts.BestTitle(folder, "IL2Game", "mc_pitch", "inpmc_pitch"));
        Assert.Equal("Mouse control: flight direction up/down", ActionTexts.BestTitle(folder, "IL2Korea", "mc_pitch", "Mc pitch"));
        // IL-2 Korea: always the table (GB's "Patrol the area" is a gesture in Korea).
        Assert.Equal("Gesture: hand up", ActionTexts.BestTitle(folder, "IL2Korea", "bc_signal_patrol", "Patrol the area"));
        Assert.Equal("Order: patrol the area", ActionTexts.BestTitle(folder, "IL2Game", "bc_signal_patrol", string.Empty));
        // Unknown id: the title as it is.
        Assert.Equal("Something", ActionTexts.BestTitle(folder, "IL2Korea", "no_such_action", "Something"));
    }

    // MSFS 2024, Star Citizen and X-Plane: the table only corrects shorthand of the game's titles, so its name always
    // wins; other ids keep the game's title. No name of these tables has an unexplained abbreviation or is only a word
    // and an axis letter.
    [Fact]
    public void CorrectionTables_NameWinsAndIsReadable()
    {
        string folder = ShippedFolder();
        ActionTexts.Reset();
        Assert.Equal("Electronic Flight Bag (EFB): Back", ActionTexts.BestTitle(folder, "MSFS2024", "KEY_EFB_BACK", "EFB Back"));
        Assert.Equal("Electronic Flight Bag (EFB): Next App", ActionTexts.BestTitle(folder, "MSFS2024", "KEY_EFB_NAV_APP_NEXT", "EFB Previous Next"));
        Assert.Equal("Photo Mode Camera: Roll Left/Right Axis", ActionTexts.BestTitle(folder, "MSFS2024", "KEY_PHOTO_MODE_AXIS_ROTATION_Z", "Rotate Z Axis"));
        // The sim's menu and description say "CFD ON" for UI_DISPLAY_CFD_OFF: the name follows the sim's menu.
        Assert.Equal("Computational Fluid Dynamics (CFD) Display On", ActionTexts.BestTitle(folder, "MSFS2024", "KEY_UI_DISPLAY_CFD_OFF", "Cfd On"));
        Assert.Equal("Elevator Axis", ActionTexts.BestTitle(folder, "MSFS2024", "KEY_AXIS_ELEVATOR_SET", "Elevator Axis"));
        Assert.Equal("Weapon presets: set quantum interdictors (QIDs)", ActionTexts.BestTitle(folder, "StarCitizen", "spaceship_weapons|v_weapon_preset_qid", "Weapon preset qid"));
        Assert.Equal("Autopilot ALT/VVI selector display: show altitude", ActionTexts.BestTitle(folder, "XPlane12", "sim/autopilot/set_ott_seldisp_ALT_VVI_alt", "Let ott seldisp ALT VVI show ALT"));
        // MSFS 2020 has no correction table: its own title stays.
        Assert.Equal("EFB Back", ActionTexts.BestTitle(folder, "MSFS2020", "KEY_EFB_BACK", "EFB Back"));
        foreach (string game in new[] { "MSFS2024", "StarCitizen", "XPlane11", "XPlane12" })
        {
            Dictionary<string, ActionTexts.Entry> table = ActionTexts.Table(folder, game);
            Assert.NotEmpty(table);
            foreach (ActionTexts.Entry e in table.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name), game + " " + e.Id + ": no name");
                Assert.Null(ActionTexts.UnexplainedAbbreviation(game, e.Id, e.Name));
                Assert.False(ActionTexts.IsWordPlusAxis(e.Name), game + " " + e.Id + ": " + e.Name);
                Assert.True(e.Category.Length == 0, game + " " + e.Id + ": a correction table keeps the game's category");
            }
        }
    }

    // Name and explanation coverage of the shipped tables: every IL-2 Great Battles and IL-2 Korea action of the
    // development catalogs (and of the game files the tables were written from) has a readable name, a menu section and
    // an explanation; no name has an unexplained abbreviation.
    [Fact]
    public void Il2Tables_NameAndExplainEveryAction()
    {
        string folder = ShippedFolder();
        ActionTexts.Reset();
        foreach (string game in new[] { "IL2Game", "IL2Korea" })
        {
            Dictionary<string, ActionTexts.Entry> table = ActionTexts.Table(folder, game);
            Assert.True(table.Count > 350, game + ": " + table.Count + " entries");
            foreach (ActionTexts.Entry e in table.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name), game + " " + e.Id + ": no name");
                Assert.False(string.IsNullOrWhiteSpace(e.Category), game + " " + e.Id + ": no menu section");
                Assert.True(e.Explanation.Length >= 20, game + " " + e.Id + ": explanation too short: " + e.Explanation);
                Assert.Null(ActionTexts.UnexplainedAbbreviation(game, e.Id, e.Name));
                Assert.False(ActionTexts.IsWordPlusAxis(e.Name), game + " " + e.Id + ": " + e.Name);
            }
        }
        // The development catalogs (private repository only).
        string il2 = Path.Combine(RepoRoot(), "EngineOnly_Runtime", "DB", "IL2", "IL2.actions");
        if (File.Exists(il2))
        {
            Dictionary<string, ActionTexts.Entry> gb = ActionTexts.Table(folder, "IL2Game");
            List<string> missing = File.ReadAllLines(il2).Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^([a-z0-9_]+),.*//[ab]").Groups[1].Value)
                .Where(id => id.Length > 0 && !gb.ContainsKey(id)).Distinct().ToList();
            Assert.True(missing.Count == 0, "IL-2 GB actions without JoyPro's text: " + string.Join(", ", missing));
        }
        string korea = Path.Combine(RepoRoot(), "EngineOnly_Runtime", "DB", "IL2Korea", "actions.tsv");
        if (File.Exists(korea))
        {
            Dictionary<string, ActionTexts.Entry> k = ActionTexts.Table(folder, "IL2Korea");
            List<string> missing = File.ReadAllLines(korea).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')[0])
                .Where(id => !k.ContainsKey(id)).ToList();
            Assert.True(missing.Count == 0, "IL-2 Korea actions without JoyPro's text: " + string.Join(", ", missing));
        }
    }

    [Fact]
    public void Il2Tables_ExplainThePrefixesNobodyKnows()
    {
        string folder = ShippedFolder();
        ActionTexts.Reset();
        foreach (string game in new[] { "IL2Game", "IL2Korea" })
        {
            Assert.StartsWith("Mouse turret aim", ActionTexts.Find(folder, game, "rb_pitch")!.Name);
            Assert.StartsWith("Mouse control", ActionTexts.Find(folder, game, "mc_yaw")!.Name);
            Assert.StartsWith("Mouse control", ActionTexts.Find(folder, game, "cam_switch_mc_far")!.Name);
            Assert.StartsWith("Mission panel key", ActionTexts.Find(folder, game, "ext_action1")!.Name);
            Assert.Equal("Roll (ailerons)", ActionTexts.Find(folder, game, "rpc_roll")!.Name);
            Assert.Contains("mouse", ActionTexts.Find(folder, game, "rb_yaw")!.Explanation);
        }
        Assert.Equal("Plane controls", ActionTexts.Find(folder, "IL2Game", "rpc_pitch")!.Category);
        Assert.Equal("Flight Controls", ActionTexts.Find(folder, "IL2Korea", "rpc_pitch")!.Category);
    }

    // The relation editor's node titles (ResolveNodeTitle): an IL-2 Korea node shows JoyPro's name, never the raw key or
    // IL-2 Great Battles' title of another meaning.
    [Fact]
    public void RelationEditor_NamesIl2KoreaNodes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "jp-actiontexts-" + Guid.NewGuid().ToString("N"));
        try
        {
            string texts = Path.Combine(folder, "DB", "Functions", "ActionTexts");
            Directory.CreateDirectory(texts);
            File.Copy(Path.Combine(ShippedFolder(), "IL2Korea.tsv"), Path.Combine(texts, "IL2Korea.tsv"));
            File.Copy(Path.Combine(ShippedFolder(), "IL2Game.tsv"), Path.Combine(texts, "IL2Game.tsv"));
            string engine = Path.Combine(folder, "engine.exe");
            File.WriteAllText(engine, string.Empty);
            ActionTexts.Reset();
            MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
            Assert.Equal("Mouse turret aim: up/down", vm.ResolveNodeTitleForTests("IL2Korea", "IL2Korea", "rb_pitch"));
            Assert.Equal("Gesture: hand up", vm.ResolveNodeTitleForTests("IL2Korea", "IL2Korea", "bc_signal_patrol"));
            Assert.Equal("Mouse control: free look left/right", vm.ResolveNodeTitleForTests("IL2Game", "IL2Game", "mc_yaw_free"));
        }
        finally
        {
            ActionTexts.Reset();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // Per game with the real engine and the development catalogs: no unexplained abbreviation and every action explained
    // in IL-2 GB and Korea; elsewhere only the few left whose meaning no game file states (listed).
    [Fact]
    public void Coverage_RealEngine_ReadableTitlesPerGame()
    {
        string? engine = DuplicateFixture.EnginePath();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "jp-actioncoverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "coverage.json");
            ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
            foreach (string arg in new[] { "--out", output, "relations", "action-info", "--coverage" })
                info.ArgumentList.Add(arg);
            info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data");
            info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(folder, "logs");
            info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
            info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
            using (Process process = Process.Start(info)!)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                Assert.True(process.WaitForExit(300000), "engine coverage did not finish");
                Assert.Equal(0, process.ExitCode);
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(output));
            Dictionary<string, (int Actions, int Abbreviated, int WordAxis, int JoyPro)> rows = doc.RootElement.GetProperty("Games").EnumerateArray()
                .ToDictionary(g => g.GetProperty("Game").GetString()!, g => (g.GetProperty("Actions").GetInt32(), g.GetProperty("Abbreviated").GetInt32(), g.GetProperty("WordAxis").GetInt32(), g.GetProperty("JoyProText").GetInt32()));
            foreach (string game in new[] { "IL2Game", "IL2Korea" })
            {
                Assert.True(rows.ContainsKey(game), game + " missing in the coverage");
                (int actions, int abbreviated, int wordAxis, int joyPro) = rows[game];
                Assert.True(abbreviated == 0 && wordAxis == 0, game + ": " + abbreviated + " titles with abbreviations, " + wordAxis + " word+axis");
                Assert.True(joyPro == actions, game + ": " + joyPro + " of " + actions + " actions explained by JoyPro's table");
            }
            // What no game file explains (IL-2 CoD "Len"): at most these. Star Citizen's QIDs and IFCS inputs and X-Plane's
            // "ott seldisp" are named by JoyPro's tables now.
            Dictionary<string, int> allowed = new(StringComparer.OrdinalIgnoreCase) { ["IL2CoD"] = 1, ["IL2CoDBlitz"] = 1 };
            foreach ((string game, (int actions, int abbreviated, int wordAxis, int _)) in rows.Where(r => r.Key != "DCS"))
            {
                int limit = allowed.TryGetValue(game, out int a) ? a : 0;
                Assert.True(abbreviated + wordAxis <= limit, game + ": " + abbreviated + " titles with unexplained abbreviations and " + wordAxis + " word+axis titles (allowed " + limit + ")");
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
