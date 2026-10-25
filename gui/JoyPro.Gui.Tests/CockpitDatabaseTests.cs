using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The cockpit database (EngineOnly_Runtime\DB\Cockpits, docs/cockpits.md) and the Cockpits page.
[SupportedOSPlatform("windows")]
public sealed class CockpitDatabaseTests
{
    public static string ShippedCockpits => Path.Combine(Path.GetDirectoryName(DeviceReferenceTests.ShippedCatalog)!, "Cockpits");

    private static HashSet<string> FunctionIds()
    {
        string file = Path.Combine(Path.GetDirectoryName(DeviceReferenceTests.ShippedCatalog)!, "Functions", "slots.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        return new HashSet<string>(doc.RootElement.EnumerateArray().Select(s => s.GetProperty("id").GetString()!), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Database_PassesEveryCheck()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        Assert.NotEmpty(catalog.Aircraft);
        Assert.True(catalog.Drawings.Count >= 11, "the generic drawings are missing");
        List<string> problems = catalog.Check(FunctionIds());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(60)));
    }

    // No area boxes and no traced drawings: without pictures from a game the overview is its numbered list, a grip page
    // keeps JoyPro's clean vector drawing of the grip and its HOTAS table.
    [Fact]
    public void Pages_WithoutPictures_ShowNoAreaBoxesNorTracedDrawings_ButTheGripsVectorDrawing()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        CockpitVariant v = catalog.Resolve(catalog.Aircraft.First(a => a.Id == "f-16").Variants.First(x => x.Id == "f-16c-block-50"));
        ManualPageOptions o = new() { Folder = ShippedCockpits };
        ManualPage overview = CockpitManualPages.Overview(catalog, v, o);
        Assert.DoesNotContain(overview.Items, i => i.Kind == "drawing");
        Assert.Contains(overview.Items, i => i.Kind == "text" && i.Lines.Any(l => l.Length > 0));
        ManualPage stick = CockpitManualPages.Device(catalog, v, "stick", o)!;
        ManualPageItem drawing = Assert.Single(stick.Items, i => i.Kind == "drawing");
        Assert.Null(drawing.Art!.LineArt);
        foreach (CockpitPanel p in v.Panels)
            Assert.DoesNotContain(CockpitManualPages.Panel(catalog, v, p, o).Items, i => i.Kind == "drawing");
    }

    [Theory]
    [InlineData("laminar/B738/switch/gen2_up", "Gen 2 up")]
    [InlineData("sim/electrical/battery_1_on", "Battery 1 on (electrical)")]
    [InlineData("laminar/B738/autopilot/cmd_a_press", "Cmd a press (autopilot)")]
    [InlineData("sim/cockpit2/switches/instrument_brightness_ratio[4]", "Instrument brightness ratio 4")]
    [InlineData("JFS Switch - START 1 / OFF", "JFS Switch - START 1 / OFF")]
    public void CommandName_MakesXPlaneCommandPathsReadable(string id, string name)
    {
        Assert.Equal(name, CockpitManualPages.CommandName(id));
    }

    // A Star Citizen ship has no type number, so the picture sets are found only by the game's own ship name
    // ("Aegis Gladius"): every listed Star Citizen ship names it.
    [Fact]
    public void EveryStarCitizenShip_NamesItsShipInTheGame()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        List<string> missing = catalog.Aircraft.Where(a => !a.Hidden).SelectMany(a => a.Variants)
            .Where(v => v.Games.Any(g => g.Game == "StarCitizen") && v.Games.Where(g => g.Game == "StarCitizen").All(g => string.IsNullOrWhiteSpace(g.Plane)))
            .Select(v => v.Id).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void EveryVariant_HasStickAndThrottleControls_FunctionsSourcesAndConfidence()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        HashSet<string> ids = FunctionIds();
        List<string> problems = [];
        foreach (CockpitAircraft a in catalog.Aircraft)
        {
            foreach (CockpitVariant v in a.Variants.Select(catalog.Resolve))
            {
                string at = a.Id + "/" + v.Id;
                if (!v.Controls.Any(c => CockpitCatalog.IsStickDevice(c.Device) && c.Functions.Count > 0))
                    problems.Add(at + ": no stick-side control");
                if (!v.Controls.Any(c => CockpitCatalog.IsThrottleDevice(c.Device) && c.Functions.Count > 0))
                    problems.Add(at + ": no throttle-side control");
                if (!a.Hidden && v.Sources.Count == 0)
                    problems.Add(at + ": no source");
                if (!a.Hidden && v.Confidence is not ("high" or "medium" or "low"))
                    problems.Add(at + ": no confidence");
                foreach (CockpitFunction f in v.Controls.SelectMany(c => c.Functions).Where(f => f.Fn.Length > 0 && !ids.Contains(f.Fn)))
                    problems.Add(at + ": unknown function id " + f.Fn);
                if (a.Fiction && v.Drawings.Values.Any(d => catalog.Drawings.TryGetValue(d, out CockpitDrawing? drawing) && !drawing.Generic))
                    problems.Add(at + ": a fictional craft with a drawing of its own");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(60)));
    }

    [Fact]
    public void EveryVariant_BuildsCleanSheets()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        List<string> problems = [];
        int sheets = 0;
        foreach (CockpitAircraft a in catalog.Aircraft)
        {
            foreach (CockpitVariant v in a.Variants.Select(catalog.Resolve))
            {
                foreach (CockpitSheet sheet in CockpitSchematic.SheetsOf(catalog, v))
                {
                    sheets++;
                    CalloutCheck check = CalloutLayouter.Check(sheet.Sheet);
                    if (check.Overlaps > 0 || check.Outside > 0 || check.NonOrthogonal > 0)
                        problems.Add(a.Id + "/" + v.Id + " " + sheet.Device + ": " + check);
                    if (sheet.Drawing.Generic && sheet.Unplaced > 0)
                        problems.Add(a.Id + "/" + v.Id + " " + sheet.Device + ": the generic drawing has no part left for " + sheet.Unplaced + " control(s)");
                }
            }
        }
        Assert.True(sheets > 0);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(60)));
    }

    [Fact]
    public void Drawings_HaveValidPathsAndPartsInside()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        foreach (CockpitDrawing d in catalog.Drawings.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Origin), d.Id + " says nothing about how it was drawn");
            foreach (SchematicShape s in d.Shapes.Where(s => s.Kind == "path"))
                Assert.True(CockpitCatalog.ValidPath(s.Path), d.Id + ": " + s.Path);
            foreach (CockpitDrawingPart p in d.Parts)
                Assert.True(p.X - p.R >= 0 && p.Y - p.R >= 0 && p.X + p.R <= d.Width && p.Y + p.R <= d.Height, d.Id + ": " + p.Id + " reaches out");
            // Every glyph is a valid shape too.
            foreach (SchematicShape s in CockpitSchematic.Drawing(d).Shapes.Where(s => s.Kind == "path"))
                Assert.True(CockpitCatalog.ValidPath(s.Path), d.Id + ": glyph " + s.Path);
        }
        foreach (string generic in new[] { "generic-stick", "generic-throttle", "generic-sidestick", "generic-yoke", "generic-quadrant", "generic-cyclic", "generic-collective", "generic-space-stick", "generic-space-throttle", "generic-pedals", "generic-panel" })
            Assert.True(catalog.Drawings.TryGetValue(generic, out CockpitDrawing? drawing) && drawing.Generic, generic + " is missing");
    }

    [Fact]
    public void Photos_AreHttpsLinksOnly_AndNoPictureIsInTheDatabase()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        foreach (CockpitPhoto p in catalog.Aircraft.SelectMany(a => a.Photos))
        {
            Assert.True(p.Url.Length == 0 || CockpitCatalog.IsWebAddress(p.Url), p.Url);
            Assert.True(p.Page.Length == 0 || CockpitCatalog.IsWebAddress(p.Page), p.Page);
        }
        string[] pictures = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
        Assert.Empty(Directory.GetFiles(ShippedCockpits, "*", SearchOption.AllDirectories).Where(f => pictures.Contains(Path.GetExtension(f).ToLowerInvariant())));
    }

    // ------------------------------------------------------------------ the model

    private static CockpitCatalog Synthetic()
    {
        string json = """
        { "format": "joypro-cockpits", "version": 1, "aircraft": [
          { "id": "test-jet", "name": "Test Jet", "category": "fixed-wing", "era": "modern", "role": "fighter", "nations": ["US"],
            "variants": [
              { "id": "test-a", "name": "Test A", "confidence": "high", "games": [ { "game": "DCS", "plane": "F-16C_50" } ],
                "sources": [ { "title": "Manual", "url": "https://example.org/a", "kind": "manual" } ],
                "controls": [
                  { "id": "stick.hat.tms", "device": "stick", "name": "TMS", "type": "hat4", "where": "top right",
                    "functions": [ { "dir": "up", "text": "Lock", "fn": "target_management_system_up" }, { "dir": "down", "text": "Unlock" } ] },
                  { "id": "stick.trigger", "device": "stick", "name": "Trigger", "type": "trigger", "where": "front",
                    "functions": [ { "dir": "stage2", "text": "Gun", "fn": "trigger2ndstage" } ] },
                  { "id": "throttle.axis.main", "device": "throttle", "name": "Throttle", "type": "axis", "where": "left console",
                    "functions": [ { "dir": "axis", "text": "Thrust", "fn": "throttleright" } ] },
                  { "id": "throttle.speedbrake", "device": "throttle", "name": "Speed brake", "type": "switch3", "where": "thumb",
                    "functions": [ { "dir": "aft", "text": "Open", "fn": "airbakeextend" } ] } ] },
              { "id": "test-b", "name": "Test B", "inherits": "test-a", "removeControls": ["throttle.speedbrake"],
                "sources": [ { "title": "Guide", "url": "https://example.org/b", "kind": "tutorial" } ],
                "controls": [ { "id": "stick.hat.tms", "device": "stick", "name": "TMS new", "type": "hat4", "where": "top right",
                    "functions": [ { "dir": "up", "text": "Designate", "fn": "target_management_system_up" } ] },
                  { "id": "throttle.china", "device": "throttle", "name": "China hat", "type": "hat2", "where": "front",
                    "functions": [ { "dir": "fwd", "text": "Sensor fwd" } ] } ] } ] },
          { "id": "test-wing", "name": "Test Wing", "category": "space", "era": "future", "role": "starfighter", "fiction": true, "franchise": "A Film",
            "variants": [ { "id": "test-wing-1", "name": "Mk 1", "confidence": "low",
                "sources": [ { "title": "The film", "kind": "film" } ],
                "controls": [
                  { "id": "stick.fire", "device": "stick", "name": "Fire", "type": "button", "where": "thumb", "functions": [ { "dir": "", "text": "Fire lasers" } ] },
                  { "id": "stick.targeting", "device": "stick", "name": "Targeting", "type": "hat4", "where": "top", "functions": [ { "dir": "up", "text": "Next target" } ] },
                  { "id": "throttle.lever", "device": "throttle", "name": "Throttle", "type": "axis", "where": "left", "functions": [ { "dir": "axis", "text": "Speed" } ] } ] } ] } ] }
        """;
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        CockpitCatalog test = new() { Folder = "synthetic" };
        foreach (KeyValuePair<string, CockpitDrawing> d in catalog.Drawings)
            test.Drawings[d.Key] = d.Value;
        test.AddFile((Dictionary<string, object>)MiniJson.Parse(json), "synthetic.json");
        return test;
    }

    [Fact]
    public void Inheritance_ReplacesAddsAndRemovesControls_AndJoinsSources()
    {
        CockpitCatalog catalog = Synthetic();
        CockpitVariant b = catalog.FindVariant("test-jet", "Test B")!;
        Assert.Equal("test-b", b.Id);
        Assert.Equal("TMS new", b.Controls.Single(c => c.Id == "stick.hat.tms").Name);
        Assert.Contains(b.Controls, c => c.Id == "throttle.china");
        Assert.DoesNotContain(b.Controls, c => c.Id == "throttle.speedbrake");
        Assert.Contains(b.Controls, c => c.Id == "stick.trigger");
        Assert.Equal(2, b.Sources.Count);
        Assert.Equal("high", b.Confidence);
        Assert.Equal("DCS", b.Games.Single().Game);
        // A variant id alone finds its aircraft.
        Assert.Equal("test-b", catalog.FindVariant("test-b", null)!.Id);
        // The synthetic variants have no overview or panels: only the structure is checked.
        Assert.Empty(catalog.Check(FunctionIds(), complete: false).Where(p => p.StartsWith("test-", StringComparison.Ordinal)));
        Assert.Contains(catalog.Check(FunctionIds()), p => p.StartsWith("test-jet/test-a: no overview", StringComparison.Ordinal));
    }

    // A variant as the manual shows it: modes (sensors of interest) as the HOTAS table's columns, short and long presses,
    // a ground / air row, the overview's numbered areas, numbered panels, a seat, sources per item.
    private static CockpitCatalog Manual()
    {
        string json = """
        { "format": "joypro-cockpits", "version": 2, "aircraft": [
          { "id": "test-hog", "name": "Test Hog", "category": "fixed-wing", "era": "modern", "role": "attack",
            "variants": [ { "id": "test-hog-c", "name": "C", "confidence": "high", "games": [ { "game": "DCS", "plane": "TestHog" } ],
                "sources": [ { "id": "m1", "title": "Test manual", "kind": "game-manual", "file": "Doc/test.pdf" } ],
                "modes": [ { "id": "tad", "name": "TAD", "kind": "soi" }, { "id": "tgp", "name": "TGP", "kind": "soi" }, { "id": "hud", "name": "HUD", "kind": "soi" } ],
                "defaultMode": "hud",
                "overview": { "text": "Two consoles and a front dash.", "areas": [
                    { "n": 1, "name": "Front dash", "area": "front-center", "panels": ["front"] },
                    { "n": 2, "name": "Left console", "area": "left-console", "panels": ["fuel"], "x": 0.2, "y": 0.8 } ] },
                "panels": [
                  { "id": "front", "name": "Front dash", "area": "front-center", "src": "m1", "page": "113",
                    "items": [ { "n": 1, "name": "Gear handle", "type": "handle", "positions": ["UP", "DN"], "text": "Raises and lowers the gear." },
                               { "n": 2, "name": "Clock", "type": "gauge" } ] },
                  { "id": "fuel", "name": "Fuel panel", "area": "left-console", "items": [ { "n": 1, "name": "Boost pumps", "type": "switch2" } ] } ],
                "controls": [
                  { "id": "stick.hat.dms", "device": "stick", "name": "DMS", "type": "hat4", "where": "top", "n": 2, "src": "m1", "page": "109",
                    "functions": [
                      { "dir": "fwd", "press": "short", "by": { "tad": "Scale down", "tgp": "Zoom in", "hud": "Steerpoint up" }, "fnBy": { "hud": "a10_dms_up" } },
                      { "dir": "fwd", "press": "long", "by": { "tad": "Map toggle", "tgp": "Focus", "hud": "" } },
                      { "dir": "left", "press": "long", "text": "HMD on/off" } ] },
                  { "id": "stick.button.nws", "device": "stick", "name": "NWS", "type": "button", "where": "front", "n": 4,
                    "functions": [ { "dir": "press", "when": "ground", "text": "Nosewheel steering" }, { "dir": "press", "when": "air", "text": "AR disconnect" } ] },
                  { "id": "throttle.china", "device": "throttle", "name": "China hat", "type": "hat2", "where": "left", "n": 4,
                    "functions": [ { "dir": "fwd", "press": "long", "text": "Slave all to SPI" } ] } ] },
              { "id": "test-hog-cpg", "name": "C rear seat", "seat": "CPG", "seatOf": "test-hog-c", "inherits": "test-hog-c" } ] } ] }
        """;
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        CockpitCatalog test = new() { Folder = "synthetic" };
        foreach (KeyValuePair<string, CockpitDrawing> d in catalog.Drawings)
            test.Drawings[d.Key] = d.Value;
        test.AddFile((Dictionary<string, object>)MiniJson.Parse(json), "manual.json");
        return test;
    }

    [Fact]
    public void ManualVariant_ChecksModesAreasAndPanels_AndBuildsTheManualPages()
    {
        CockpitCatalog catalog = Manual();
        Assert.Empty(catalog.Check(FunctionIds()).Where(p => p.StartsWith("test-hog", StringComparison.Ordinal)));
        CockpitVariant v = catalog.FindVariant("test-hog", "test-hog-c")!;
        CockpitFunction shortFwd = v.Controls.Single(c => c.Id == "stick.hat.dms").Functions[0];
        // The default mode's cell is the row's text for the one-column views and the setup wizard.
        Assert.Equal("Steerpoint up", shortFwd.Text);
        Assert.Equal("a10_dms_up", shortFwd.Fn);
        Assert.Equal("Zoom in", shortFwd.TextIn("tgp"));
        Assert.Equal(string.Empty, shortFwd.FnIn("tgp"));
        Assert.Equal("Test manual, p. 113", v.Cite("m1", "113"));
        // The setup wizard places a function where the default mode's column has it.
        JoyPro.Setup.CockpitPositionTable table = new();
        table.AddCatalog(catalog, "test");
        Assert.True(table.TryGet("TestHog", "a10_dms_up", out List<string>? dms));
        Assert.Contains("stick.hat.dms.up", dms!);
        // The seat inherits the overview and the panels and names its version.
        CockpitVariant cpg = catalog.FindVariant("test-hog", "test-hog-cpg")!;
        Assert.Equal("test-hog-c", cpg.SeatOf);
        Assert.Equal(2, cpg.Overview!.Areas.Count);

        List<ManualPage> pages = CockpitManualPages.Build(catalog, v, new ManualPageOptions { Highlight = "zoom" });
        Assert.Equal(["overview", "device:stick", "device:throttle", "panel:front", "panel:fuel"], pages.Select(p => p.Id));
        ManualPage stick = pages[1];
        // The table: a header cell per mode, the merged "HMD on/off" across the three modes, the ground / air rows.
        Assert.Contains(stick.Items, i => i.Kind == "cell" && i.Lines.SequenceEqual(["TAD"]));
        ManualPageItem hmd = stick.Items.Single(i => i.Kind == "cell" && i.Lines.SequenceEqual(["HMD on/off"]));
        ManualPageItem tad = stick.Items.Single(i => i.Kind == "cell" && i.Lines.SequenceEqual(["TAD"]));
        Assert.True(hmd.W > tad.W * 2.5, "the cell spans the three modes");
        Assert.Contains(stick.Items, i => i.Kind == "cell" && string.Join(" ", i.Lines).Contains("ground: Depress", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stick.Items, i => i.Highlight && i.Lines.SequenceEqual(["Zoom in"]));
        Assert.Contains(stick.Items, i => i.Tag == "fn:a10_dms_up");
        Assert.Contains("Steerpoint up", stick.SearchText);
        // The overview's areas link to their panel pages; the panel page lists the items with their positions.
        Assert.Contains(pages[0].Items, i => i.Link == "panel:fuel");
        Assert.Contains("Positions: UP / DN.", string.Join(" ", pages[3].Items.SelectMany(i => i.Lines)));

        // A row without a cell for every mode and a panel numbered with a gap are reported.
        CockpitVariant raw = catalog.Aircraft.Single(a => a.Id == "test-hog").Variants[0];
        raw.Controls[0].Functions[1].By.Remove("tgp");
        raw.Panels[0].Items[1].N = 3;
        catalog.AddFile(new Dictionary<string, object> { ["aircraft"] = new List<object>() }, "touch.json");
        List<string> problems = catalog.Check(FunctionIds());
        Assert.Contains(problems, p => p.Contains("no function for mode(s) tgp", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("items are not numbered 1..2", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_FindsByNameVariantGameAndFilters()
    {
        CockpitCatalog catalog = Synthetic();
        Assert.Equal("test-jet", catalog.Search(new CockpitFilter { Query = "test b" }).First().Id);
        Assert.Equal("test-jet", catalog.Search(new CockpitFilter { Query = "F-16C_50" }).Single().Id);
        Assert.Equal("test-wing", catalog.Search(new CockpitFilter { Category = "fiction" }).Single().Id);
        Assert.Equal("test-jet", catalog.Search(new CockpitFilter { Category = "real", Game = "DCS" }).Single().Id);
        Assert.Empty(catalog.Search(new CockpitFilter { Era = "ww2" }));
        Assert.Contains(catalog.SearchControls("china", 10), h => h.Item1.Id == "test-b");
    }

    [Fact]
    public void FictionalCraft_GetTheGenericDrawings_WithEveryControlPlaced()
    {
        CockpitCatalog catalog = Synthetic();
        CockpitVariant v = catalog.FindVariant("test-wing", null)!;
        List<CockpitSheet> sheets = CockpitSchematic.SheetsOf(catalog, v);
        Assert.Equal(["stick", "throttle"], sheets.Select(s => s.Device));
        Assert.Equal("generic-space-stick", sheets[0].Drawing.Id);
        Assert.Equal("generic-space-throttle", sheets[1].Drawing.Id);
        Assert.All(sheets, s => Assert.Equal(0, s.Unplaced));
        Assert.All(sheets, s => Assert.All(s.Sheet.Tables, t => Assert.True(t.Known)));
        // A real aircraft's variant: rows say what the control does, axes read as axes.
        CockpitSheet stick = CockpitSchematic.SheetsOf(catalog, catalog.FindVariant("test-jet", "test-a")!)[0];
        Assert.Equal("2nd detent: Gun", stick.TextOf("cp:stick.trigger:stage2", false));
        Assert.StartsWith("JOY_AXIS:", CockpitSchematic.InputOf(new CockpitControl { Id = "throttle.axis.main", Type = "axis" }, new CockpitFunction { Dir = "axis" }));
    }

    [Fact]
    public void CommonsPhotos_LoadAsWikimediaThumbnails()
    {
        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/thumb/a/ab/F-16_cockpit.jpg/960px-F-16_cockpit.jpg",
            CockpitsViewModel.CommonsThumbnail("https://upload.wikimedia.org/wikipedia/commons/a/ab/F-16_cockpit.jpg", 960));
        Assert.Null(CockpitsViewModel.CommonsThumbnail("https://www.nasa.gov/image.jpg", 960));
        Assert.Null(CockpitsViewModel.CommonsThumbnail("https://upload.wikimedia.org/wikipedia/commons/thumb/a/ab/X.jpg/960px-X.jpg", 960));
    }

    // ------------------------------------------------------------------ the setup wizard's realism

    [Fact]
    public void SetupWizard_ReadsTheDatabaseAndTheLayoutOfCockpitsGet()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(ShippedCockpits);
        // A game's own aircraft name finds its variant (the wizard asks "cockpits get F-16C_50").
        Assert.Equal("f-16c-block-50", catalog.FindVariant("F-16C_50", null)!.Id);
        Assert.Equal("fa-18c-lot-20", catalog.FindVariant("FA-18C_hornet", null)!.Id);

        JoyPro.Setup.CockpitPositionTable table = new();
        Assert.True(table.AddCatalog(catalog, "DB\\Cockpits") > 20);
        Assert.True(table.TryGet("F-16C_50", "target_management_system_up", out List<string>? tms));
        Assert.Contains("stick.hat.tms.up", tms!);
        Assert.True(table.TryGet("F-16C_50", "pitch", out List<string>? pitch));
        Assert.Contains("stick.axis.pitch", pitch!);
        Assert.Contains("DB\\Cockpits", table.Source);

        // The JSON of "engine cockpits get <aircraft> --json" (joypro-cockpit-layout); a file the caller passes wins over
        // the database for its aircraft.
        string layout = """
        { "Format": "joypro-cockpit-layout", "Version": 1, "Variant": { "Id": "test-variant" },
          "Games": [ { "Game": "DCS", "Plane": "F-16C_50" } ],
          "Devices": [ { "Device": "stick", "Controls": [ { "Id": "stick.hat.cms",
              "Functions": [ { "Dir": "up", "Fn": "laser_1nd_detent", "Vocabulary": "stick.hat.cms.up" } ] } ] } ] }
        """;
        JoyPro.Setup.CockpitPositionTable withFile = new();
        Assert.Equal(["F-16C_50", "test-variant"], withFile.AddJson(layout, "cockpit-f16", null));
        withFile.AddCatalog(catalog, "DB\\Cockpits");
        Assert.True(withFile.TryGet("F-16C_50", "laser_1nd_detent", out List<string>? laser));
        Assert.Equal(["stick.hat.cms.up"], laser!);
        Assert.False(withFile.TryGet("F-16C_50", "target_management_system_up", out _));
        Assert.StartsWith("cockpit database: cockpit-f16", withFile.Source);
    }

    // ------------------------------------------------------------------ the page

    [Fact]
    public void Page_ShowsAVariant_ComparesAndHandsTheLayoutToTheWizard()
    {
        CockpitService.FolderOverride = () => ShippedCockpits;
        CockpitService.Reload();
        try
        {
            CockpitsViewModel vm = new();
            vm.Reload();
            Assert.NotEmpty(vm.AircraftList);
            Assert.True(vm.Show("f-16", "f-16c-block-50"));
            Assert.Equal("f-16c-block-50", vm.Variant!.Id);
            Assert.Equal(2, vm.Sheets.Count);
            Assert.Null(vm.Plan); // no plan of area boxes
            Assert.NotEmpty(vm.Sources);

            vm.ApplyCompare("""{"Aircraft":"f-16","Variant":"f-16c-block-50","Matched":1,"Total":40,"Functions":[{"Control":"stick.hat.tms","Dir":"fwd","Relations":[{"Relation":"TMS Up","Device":"Stick","Input":"JOY_BTN7"}]}]}""");
            CockpitSheetItem stick = vm.Sheets.First(s => s.Sheet.Device == "stick");
            CalloutTable tms = stick.Callouts.Sheet!.Tables.First(t => t.Id == "stick.hat.tms");
            CalloutRow up = tms.Rows.First(r => r.Input.EndsWith(":fwd", StringComparison.Ordinal));
            Assert.Contains("✓ TMS Up (JOY_BTN7)", stick.Callouts.RowText(tms, up, false));
            Assert.Contains("1 of the 40", vm.CompareStatus);
            // The manual pages: overview, stick, throttle, panels; the comparison marks the stick page's table.
            Assert.Equal("overview", vm.Pages[0].Page.Id);
            Assert.Contains(vm.Pages, p => p.Page.Id == "device:stick");
            Assert.True(vm.OpenPage("device:stick"));
            Assert.Contains("✓ TMS Up (JOY_BTN7)", vm.CurrentPage!.SearchText);
            vm.PageSearchText = "TMS";
            Assert.True(vm.HasPageHits);
            vm.PageSearchText = string.Empty;

            CockpitLayoutRequest? request = null;
            vm.UseLayoutRequested += (_, r) => request = r;
            vm.UseLayoutCommand.Execute(null);
            Assert.NotNull(request);
            Assert.Equal(["cockpits", "get", "f-16", "f-16c-block-50", "--json"], request!.EngineArguments);
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    [Fact]
    public void MainWindow_HasTheCockpitsTab_AndKeepsTheLayoutForTheWizard()
    {
        CockpitService.FolderOverride = () => ShippedCockpits;
        CockpitService.Reload();
        try
        {
            MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
            vm.GoCockpitsCommand.Execute(null);
            Assert.True(vm.IsCockpitsStage);
            Assert.NotEmpty(vm.Cockpits.AircraftList);
            Assert.True(vm.Cockpits.Show("f-16"));
            CockpitLayoutRequest? raised = null;
            vm.CockpitLayoutRequested += (_, r) => raised = r;
            vm.Cockpits.UseLayoutCommand.Execute(null);
            Assert.NotNull(raised);
            Assert.Same(raised, vm.TakePendingCockpitLayout());
            Assert.Null(vm.PendingCockpitLayout);
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    [Fact]
    public void Page_DownloadsNoPhoto_WithoutConsent()
    {
        string data = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "cockpit-consent-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        Func<string, bool> mayLoad = DeviceImageCache.MayLoad;
        List<string> downloads = [];
        Action<string>? hook = DeviceImageCache.Downloads;
        DeviceImageCache.Downloads = downloads.Add;
        try
        {
            Assert.Equal(PhotoConsentState.Ask, CockpitPhotoConsent.For("f-16"));
            CockpitPhotoConsent.Answer("f-16", "yes");
            Assert.Equal(PhotoConsentState.Allowed, CockpitPhotoConsent.For("f-16"));
            Assert.Equal(PhotoConsentState.Ask, CockpitPhotoConsent.For("f-15"));
            CockpitPhotoConsent.Answer(null, "always");
            Assert.Equal(PhotoConsentState.Allowed, CockpitPhotoConsent.For("f-15"));
            CockpitPhotoConsent.Answer("f-15", "no");
            Assert.Equal(PhotoConsentState.Denied, CockpitPhotoConsent.For("f-15"));
            // The device photos' global "Never" stops cockpit photos too.
            DevicePhotoConsent.Mode = DevicePhotoConsent.Never;
            Assert.Equal(PhotoConsentState.Denied, CockpitPhotoConsent.For("f-16"));
            DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
            CockpitPhotoConsent.Answer(null, "never");
            Assert.Equal(PhotoConsentState.Allowed, CockpitPhotoConsent.For("f-16"));
            Assert.Equal(PhotoConsentState.Denied, CockpitPhotoConsent.For("a-10"));
            // The page asks and loads nothing while the answer is "ask" (tests also block every internet picture).
            CockpitService.FolderOverride = () => ShippedCockpits;
            CockpitService.Reload();
            DevicePhotoConsent.ForgetAnswers();
            CockpitsViewModel vm = new();
            vm.Reload();
            foreach (CockpitAircraftItem item in vm.AircraftList.Where(a => a.Aircraft.Photos.Count > 0).Take(5).ToList())
            {
                vm.SelectedAircraft = item;
                Assert.True(vm.AskPhotos);
                Assert.All(vm.Photos, p => Assert.Null(p.Image));
            }
            Assert.Empty(downloads);
        }
        finally
        {
            DeviceImageCache.Downloads = hook;
            DeviceImageCache.MayLoad = mayLoad;
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    // Offscreen pictures of the page with a few aircraft (drawings only: no photo is downloaded in the tests). Written
    // when JOYPRO_COCKPITS_PREVIEW names a folder; the render itself always runs.
    [Fact]
    public void Page_RendersOffscreen()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_COCKPITS_PREVIEW");
        string folder = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "cockpits-render") : outDir;
        Directory.CreateDirectory(folder);
        CockpitService.FolderOverride = () => ShippedCockpits;
        CockpitService.Reload();
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                CockpitsViewModel vm = new();
                vm.Reload();
                List<(string Aircraft, string? Variant)> picks = [("f-16", "f-16c-block-50")];
                foreach (string id in new[] { "fa-18", "ah-64", "a320", "x-wing", "spitfire", "space-shuttle" })
                {
                    if (CockpitService.Catalog.Find(id) != null)
                        picks.Add((id, null));
                }
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((string aircraft, string? v) in picks)
                    {
                        Assert.True(vm.Show(aircraft, v), aircraft);
                        string file = Path.Combine(folder, "cockpits-" + aircraft + "-" + theme + ".png");
                        Render(new CockpitsView { DataContext = vm }, 1600, 1500, file, theme == "dark");
                        Assert.True(new FileInfo(file).Length > 20000, file + " is nearly empty");
                        if (theme == "light")
                            break;
                    }
                }
                // The manual pages on their own (overview, grips with the HOTAS tables, panels), as exported.
                foreach (string id in new[] { "a-10c-ii", "f-16c-block-50", "ah-64d-pilot", "p-51d" })
                {
                    CockpitVariant? v = CockpitService.Catalog.Variant(id);
                    if (v == null)
                        continue;
                    foreach (ManualPage page in CockpitManualPages.Build(CockpitService.Catalog, v, new ManualPageOptions { Folder = ShippedCockpits }).Take(6))
                    {
                        string file = Path.Combine(folder, "cockpit-page-" + id + "-" + CockpitCatalog.Slug(page.Id) + ".png");
                        JoyPro.Gui.Views.Controls.ManualPageView.SavePng(page, file, 1.0, DrawingStyle.Default, false);
                        Assert.True(new FileInfo(file).Length > 5000, file + " is nearly empty");
                    }
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            });
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    private static void Render(Control control, int width, int height, string file, bool dark)
    {
        Size size = new(width, height);
        // An opaque page behind the text (text drawn on a transparent bitmap smears in the light theme).
        Border page = new() { Background = new SolidColorBrush(dark ? Color.Parse("#202020") : Color.Parse("#F3F3F3")), Child = control };
        Window host = new() { Content = page, Width = width, Height = height };
        for (int pass = 0; pass < 6; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(page);
        bitmap.Save(file);
        page.Child = null;
        host.Content = null;
    }
}
