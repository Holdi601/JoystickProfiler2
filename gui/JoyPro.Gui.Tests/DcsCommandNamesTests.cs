using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using JoyPro.Engine;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Readable DCS command names (engine/DcsCommandNames.cs, shared by the engine and the GUI): localization keys
// ("Input.SpitfireLFMkIX.pitch_minus", "29Fulcrum:Inp:...", "29Fulcrum:Cat:...") take DCS's English text
// (l10n/en/LC_MESSAGES/input.mo), else the profile's stored title, else a sibling file's title, else a readable form;
// raw ids and keys are never shown. Plus the tiered sort of the catalog and the action search.
[SupportedOSPlatform("windows")]
public sealed class DcsCommandNamesTests
{
    // A gettext .mo file as DCS ships it (little-endian, sorted keys).
    internal static byte[] Mo(IDictionary<string, string> entries)
    {
        List<string> keys = entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        int count = keys.Count;
        int originals = 28, translations = originals + 8 * count, data = translations + 8 * count;
        List<byte> strings = [];
        List<(int Length, int Offset)> keyTable = [], valueTable = [];
        foreach (string key in keys)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            keyTable.Add((bytes.Length, data + strings.Count));
            strings.AddRange(bytes);
            strings.Add(0);
        }
        foreach (string key in keys)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(entries[key]);
            valueTable.Add((bytes.Length, data + strings.Count));
            strings.AddRange(bytes);
            strings.Add(0);
        }
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (uint value in new uint[] { 0x950412de, 0, (uint)count, (uint)originals, (uint)translations, 0, (uint)data })
            writer.Write(value);
        foreach ((int length, int offset) in keyTable.Concat(valueTable))
        {
            writer.Write((uint)length);
            writer.Write((uint)offset);
        }
        writer.Write(strings.ToArray());
        writer.Flush();
        return stream.ToArray();
    }

    private static readonly Dictionary<string, string> English = DcsCommandNames.ParseMo(Mo(new Dictionary<string, string>
    {
        [""] = "Project-Id-Version: 1.0",
        ["Input.SpitfireLFMkIX.pitch_minus"] = "Engine RPM DECREASE",
        ["Input.SpitfireLFMkIX.pitch_slider"] = "Engine RPM (analog)",
        ["29Fulcrum:Inp:AB Lock Latch (Both) - Depress/Release"] = "AB Lock Latch (Both) - Depress/Release",
        ["29Fulcrum:Cat:Right Wall"] = "Right Wall",
        ["Throttle Up"] = "Throttle Levers - UP",
    }));

    [Fact]
    public void Mo_IsReadLikeGettext()
    {
        Assert.Equal("Engine RPM DECREASE", English["Input.SpitfireLFMkIX.pitch_minus"]);
        Assert.Equal("Right Wall", English["29Fulcrum:Cat:Right Wall"]);
        Assert.False(English.ContainsKey(string.Empty));
        Assert.Empty(DcsCommandNames.ParseMo(new byte[] { 1, 2, 3 }));
        Assert.Empty(DcsCommandNames.ReadMoFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".mo")));
    }

    [Fact]
    public void RawTitles_AreKeysIdsAndEmptyText()
    {
        Assert.True(DcsCommandNames.IsRawTitle("Input.SpitfireLFMkIX.pitch_minus"));
        Assert.True(DcsCommandNames.IsRawTitle("29Fulcrum:Inp:AC Generator Switch - OFF"));
        Assert.True(DcsCommandNames.IsRawTitle("d3114pnilunilcd63vd0.7vpnilvunil"));
        Assert.True(DcsCommandNames.IsRawTitle("a2001cdnil", "a2001cdnil"));
        Assert.True(DcsCommandNames.IsRawTitle(""));
        Assert.True(DcsCommandNames.HasLocalizationKey("29Fulcrum:Cat:Right Wall; 29Fulcrum:Cat:Electrical Power Panel"));
        Assert.False(DcsCommandNames.IsRawTitle("Flaps STOL", "d1"));
        Assert.False(DcsCommandNames.IsRawTitle("Engine RPM (analog)"));
        Assert.False(DcsCommandNames.IsRawTitle("AN/ALE-39 Flare SGL"));
        Assert.Equal("Flaps 1", DcsCommandNames.ReadableKey("Input.SpitfireLFMkIX.flaps_1"));
        Assert.Equal("AC Generator Switch - OFF", DcsCommandNames.ReadableKey("29Fulcrum:Inp:AC Generator Switch - OFF"));
        Assert.Equal("Right Wall; HUD", DcsCommandNames.ReadableKey("29Fulcrum:Cat:Right Wall; 29Fulcrum:Cat:HUD"));
    }

    // DB title -> English text of the key -> stored title -> sibling -> readable key; never the raw id.
    [Fact]
    public void Resolve_FollowsTheOrder_AndNeverReturnsTheRawId()
    {
        Assert.Equal("Gear up", DcsCommandNames.Resolve("Gear up", "d1", English, "Stored", () => "Sibling"));
        Assert.Equal("Engine RPM DECREASE", DcsCommandNames.Resolve("Input.SpitfireLFMkIX.pitch_minus", "d1", English, "Stored", () => "Sibling"));
        Assert.Equal("Stored title", DcsCommandNames.Resolve("Input.SpitfireLFMkIX.canopy", "d1", English, "Stored title", () => "Sibling"));
        Assert.Equal("Engine RPM (analog)", DcsCommandNames.Resolve("d1", "d1", English, "Input.SpitfireLFMkIX.pitch_slider"));
        Assert.Equal("Canopy EMERGENCY", DcsCommandNames.Resolve("Input.SpitfireLFMkIX.canopy", "d1", English, null, () => "Canopy EMERGENCY"));
        Assert.Equal("Canopy", DcsCommandNames.Resolve("Input.SpitfireLFMkIX.canopy", "d1", null, null, () => null));
        // Nothing readable at all: empty (the caller says the command is not in the list), never the id.
        Assert.Equal(string.Empty, DcsCommandNames.Resolve("d3114pnilunilcd63vd0.7vpnilvunil", "d3114pnilunilcd63vd0.7vpnilvunil", English, "d3114pnilunilcd63vd0.7vpnilvunil"));
        // Text that already is English stays (DCS's catalog rewords it, JoyPro keeps the source text).
        Assert.Null(DcsCommandNames.English("Throttle Up", English));
        Assert.Equal("Right Wall; Throttles", DcsCommandNames.ResolveCategory("29Fulcrum:Cat:Right Wall; Throttles", English));
        Assert.Equal("Electrical Power Panel", DcsCommandNames.ResolveCategory("29Fulcrum:Cat:Electrical Power Panel", English));
        Assert.Equal("Engine Controls", DcsCommandNames.ResolveCategory("Engine Controls", English));
    }

    // The key or the id still finds a command (its readable title is shown): "flaps_1", "pitch_minus", the whole key, a
    // DCS command id; plain words are no key search.
    [Fact]
    public void KeysAndIds_StillFindTheCommand()
    {
        Assert.True(DcsCommandNames.LooksLikeCommandId("dnilp3007unilcd2vdnilvp-1vunil"));
        Assert.True(DcsCommandNames.LooksLikeCommandId("a3008cd2"));
        Assert.True(DcsCommandNames.LooksLikeCommandId("d68pnilunilcdnilvdnilvpnilvunil"));
        Assert.False(DcsCommandNames.LooksLikeCommandId("Gear up"));
        Assert.True(DcsCommandNames.LooksLikeKeyQuery("pitch_minus"));
        Assert.True(DcsCommandNames.LooksLikeKeyQuery("dnilp3007unilcd2vdnilvp-1vunil"));
        Assert.False(DcsCommandNames.LooksLikeKeyQuery("engine rpm"));
        Assert.False(DcsCommandNames.LooksLikeKeyQuery("rpm"));
        // A refreshed list holds the English text: the catalog knows its key.
        Assert.True(DcsCommandNames.KeyMatches("pitch_minus", "Engine RPM DECREASE", English));
        Assert.True(DcsCommandNames.KeyMatches("Input.SpitfireLFMkIX.pitch_minus", "Engine RPM DECREASE", English));
        Assert.True(DcsCommandNames.KeyMatches("pitch_slider", "Engine RPM (analog)", English));
        Assert.True(DcsCommandNames.KeyMatches("29Fulcrum:Inp:AB Lock Latch (Both) - Depress/Release", "AB Lock Latch (Both) - Depress/Release", English));
        Assert.False(DcsCommandNames.KeyMatches("pitch_plus", "Engine RPM DECREASE", English));
        // An older list holds the key itself.
        Assert.True(DcsCommandNames.KeyMatches("flaps_1", "Input.SpitfireLFMkIX.flaps_1", null));
        Assert.False(DcsCommandNames.KeyMatches("flaps_1", "Flaps EXTEND", null));
        Assert.Equal(["Input.SpitfireLFMkIX.pitch_minus"], DcsCommandNames.KeysFor("Engine RPM DECREASE", English).ToArray());
    }

    // The catalog's filter box finds an action by its key (DCS's English catalog of the DCS folder JoyPro uses).
    [Fact]
    public async Task Catalog_FilterFindsTheKey()
    {
        string install = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dcs-install-" + Guid.NewGuid().ToString("N"));
        string mo = Path.Combine(install, DcsCommandNames.EnglishCatalogFiles[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(mo)!);
        File.WriteAllBytes(mo, Mo(new Dictionary<string, string> { ["Input.F16.cms_aft"] = "Countermeasures Management Switch - Aft" }));
        try
        {
            BuilderFakeEngine fake = new();
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
            vm.DcsInstallPath = install;
            await RelationBuilderTests.OpenAsync(vm, "Flare");
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0);
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            vm.CatalogFilterText = "cms_aft";
            CatalogCategoryItem hotas = vm.CatalogCategories.Single(c => c.Name == "HOTAS");
            Assert.True(hotas.IsVisible);
            Assert.Equal(["Countermeasures Management Switch - Aft"], hotas.VisibleRows.Select(r => r.Row.DisplayTitle).ToArray());
            Assert.False(vm.CatalogCategories.Single(c => c.Name == "Landing gear panel").IsVisible);
            // The id finds it too.
            vm.CatalogFilterText = "d68";
            Assert.True(vm.CatalogCategories.Single(c => c.Name == "Landing gear panel").IsVisible);
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { }
        }
    }

    // A DB file with key titles: English text first, then the sibling file's title, then the readable key.
    [Fact]
    public void LoadDcsTitleFile_UsesTheEnglishNames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dcs-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            static string Row(string title, string category, string id) =>
                "<tr>\n<td></td>\n<td>" + title + "</td>\n<td>" + category + "</td>\n<td>" + id + "</td>\n</tr>\n";
            File.WriteAllText(Path.Combine(dir, "SpitfireLFMkIX.html"), "<table>" +
                Row("Input.SpitfireLFMkIX.pitch_minus", "Engine Controls", "dnilp3007unilcd2vdnilvp-1vunil") +
                Row("Input.SpitfireLFMkIX.flaps_1", "Flight Control", "d3009pnilunilcd1vd1vpnilvunil") +
                Row("Input.SpitfireLFMkIX.canopy", "Systems", "d3010pnilunilcd1vd1vpnilvunil") + "</table>");
            File.WriteAllText(Path.Combine(dir, "SpitfireLFMkIX_easy.html"), "<table>" +
                Row("Flaps EXTEND", "Flight Control", "d3009pnilunilcd1vd1vpnilvunil") +
                Row("Engine RPM - (easy)", "Engine Controls", "dnilp3007unilcd2vdnilvp-1vunil") + "</table>");
            Dictionary<string, string> titles = MainWindowViewModel.LoadDcsTitleFile(Path.Combine(dir, "SpitfireLFMkIX.html"), English);
            Assert.Equal("Engine RPM DECREASE", titles["dnilp3007unilcd2vdnilvp-1vunil"]);
            Assert.Equal("Flaps EXTEND", titles["d3009pnilunilcd1vd1vpnilvunil"]);
            Assert.Equal("Canopy", titles["d3010pnilunilcd1vd1vpnilvunil"]);
            Assert.DoesNotContain(titles.Values, t => t.StartsWith("Input.", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // "relations show" lists what the profile stored under each node: the fallback for a command the list lost.
    [Fact]
    public void StoredNodeTitles_AreReadFromRelationsShow()
    {
        string output = "Relation: RPM\nNodes: 2\n" +
                        "  [1] game=DCS, id=d9999pnil, aircraft=SpitfireLFMkIX, SpitfireLFMkIXCW\n" +
                        "      stored-title\tSpitfireLFMkIX\tEngine RPM DECREASE\tEngine Controls\n" +
                        "      stored-title\tSpitfireLFMkIXCW\tEngine RPM DECREASE (CW)\tEngine Controls\n" +
                        "  [2] game=IL2Game, id=rpc_rpm, aircraft=IL2Game\n" +
                        "      stored-title\tIL2Game\tPropeller pitch\tEngine\n";
        Dictionary<string, string> stored = MainWindowViewModel.ParseStoredNodeTitles(output);
        Assert.Equal("Engine RPM DECREASE", MainWindowViewModel.StoredNodeTitle(stored, "d9999pnil", "SpitfireLFMkIX"));
        Assert.Equal("Engine RPM DECREASE (CW)", MainWindowViewModel.StoredNodeTitle(stored, "d9999pnil", "SpitfireLFMkIXCW"));
        Assert.Equal("Propeller pitch", MainWindowViewModel.StoredNodeTitle(stored, "rpc_rpm", "IL2Game"));
        Assert.Null(MainWindowViewModel.StoredNodeTitle(stored, "d9999pnil", "F-16C_50"));
    }

    // This PC's DCS install (read only, found as DCS registers it), when present: the Spitfire's prop pitch keys have
    // English names.
    [Fact]
    public void RealDcsInstall_HasTheEnglishNames_WhenPresent()
    {
        string install = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Eagle Dynamics\DCS World", "Path", null) as string ?? string.Empty;
        if (install.Length == 0 || !File.Exists(Path.Combine(install, DcsCommandNames.EnglishCatalogFiles[0])))
            return;
        Dictionary<string, string> catalog = DcsCommandNames.EnglishCatalog(install);
        Assert.Equal("Engine RPM DECREASE", catalog["Input.SpitfireLFMkIX.pitch_minus"]);
        Assert.Equal("Engine RPM (analog)", catalog["Input.SpitfireLFMkIX.pitch_slider"]);
        Assert.Contains(catalog.Keys, k => k.StartsWith("29Fulcrum:Inp:", StringComparison.Ordinal));
    }

    // The shipped DCS lists (EngineOnly_Runtime\DB\DCS) hold no raw key or id titles any more.
    [Fact]
    public void ShippedDcsDb_HasNoRawTitles()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "EngineOnly_Runtime", "DB", "DCS")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null)
            return;
        string db = Path.Combine(dir, "EngineOnly_Runtime", "DB", "DCS");
        List<string> raw = [];
        foreach (string file in Directory.GetFiles(db, "*.html"))
        {
            foreach (KeyValuePair<string, string> title in MainWindowViewModel.ReadDcsTitleRows(file))
            {
                if (DcsCommandNames.IsRawTitle(title.Value, title.Key))
                    raw.Add(Path.GetFileName(file) + ": " + title.Value);
            }
        }
        Assert.True(raw.Count == 0, string.Join(Environment.NewLine, raw.Take(20)));
    }

    // Action lists: game, aircraft, category, title, axis / button, last updated; no level = their own order.
    [Fact]
    public void ActionSort_HasTheActionKeys()
    {
        ListSortViewModel sort = ListSortViewModel.ForActions("catalog-test");
        Assert.Equal(["game", "plane", "category", "name", "kind", "updated"], sort.Options.Select(o => o.Key).ToArray());
        Assert.Equal("Sort: own order", sort.Label);
        DateTime old = new(2026, 1, 1), recent = new(2026, 10, 20);
        List<ListSortRow> rows =
        [
            ListSortRow.ForAction("Gear up", "DCS World", "F-16C_50", "Landing gear", false, recent),
            ListSortRow.ForAction("Engine RPM (analog)", "DCS World", "SpitfireLFMkIX", "Engine Controls", true, old),
            ListSortRow.ForAction("Engine RPM DECREASE", "DCS World", "SpitfireLFMkIX", "Engine Controls", false, old),
            ListSortRow.ForAction("Throttle", "IL-2 Great Battles", "", "Engine", true, recent),
        ];
        sort.Add("category", false);
        Assert.Equal(["Throttle", "Engine RPM (analog)", "Engine RPM DECREASE", "Gear up"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        Assert.Equal("category", sort.PrimaryKey);
        sort.Reset();
        sort.Add("updated", true);
        Assert.Equal("Newest first", sort.Levels[0].DirectionLabel);
        Assert.Equal(["Gear up", "Throttle", "Engine RPM (analog)", "Engine RPM DECREASE"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        sort.Reset();
        sort.Add("plane", false);
        sort.Add("kind", false);
        Assert.Equal(["Gear up", "Engine RPM (analog)", "Engine RPM DECREASE", "Throttle"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
    }

    // The catalog: a "Category" first level orders the categories, a "Title" level the rows in each.
    [Fact]
    public async Task Catalog_SortsCategoriesAndRows()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0);
        await vm.OpenCatalogCommand.ExecuteAsync(null);
        Assert.Equal(["HOTAS", "Stick", "Landing gear panel"], vm.CatalogCategories.Select(c => c.Name).ToArray()); // own order: categories with an action of the open relation first
        Assert.Equal(["Countermeasures Management Switch - Fwd", "Countermeasures Management Switch - Aft"], vm.CatalogCategories[0].Rows.Select(r => r.Row.Title).ToArray());

        // A DCS row never shows its command id.
        Assert.All(vm.CatalogRowItems, r => Assert.False(r.Row.HasIdHint));
        vm.CatalogSort.Add("category", true);
        Assert.Equal(["Stick", "Landing gear panel", "HOTAS"], vm.CatalogCategories.Select(c => c.Name).ToArray());
        vm.CatalogSort.Add("name", false);
        CatalogCategoryItem hotas = vm.CatalogCategories.Single(c => c.Name == "HOTAS");
        Assert.Equal(["Countermeasures Management Switch - Aft", "Countermeasures Management Switch - Fwd"], hotas.Rows.Select(r => r.Row.Title).ToArray());
        vm.CatalogSort.Reset();
        Assert.Equal(["HOTAS", "Stick", "Landing gear panel"], vm.CatalogCategories.Select(c => c.Name).ToArray()); // own order: categories with an action of the open relation first
    }

    // The action search: a "Game" first level orders the games by name, a "Title" level the rows in each game.
    [Fact]
    public async Task Search_SortsGamesAndRows()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        vm.SelectedRelation = null;
        vm.BuilderSearchText = "flare";
        await vm.SearchBuilderActionsAsync();
        List<string> Dcs() => vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Where(r => r.Game == "DCS").Select(r => r.Title).ToList();
        Assert.Equal(["DLC Toggle / Countermeasure Dispense", "AN/ALE-39 Flare SGL", "Countermeasures Management Switch - Fwd"], Dcs());

        vm.BuilderSearchSort.Add("name", false);
        Assert.Equal(["AN/ALE-39 Flare SGL", "Countermeasures Management Switch - Fwd", "DLC Toggle / Countermeasure Dispense"], Dcs());

        vm.BuilderSearchSort.SetPrimary("game");
        vm.BuilderSearchSort.SetPrimary("game");
        Assert.True(vm.BuilderSearchSort.PrimaryDescending);
        List<string> games = vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Select(g => g.Game).ToList();
        Assert.Equal(games.OrderByDescending(g => DisplayText.Game(g), Comparer<string>.Create(ListSortRow.Natural)).ToList(), games);
    }
}
