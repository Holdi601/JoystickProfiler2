using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The cockpit guide (docs/cockpits.md "The cockpit guide"): the shipped guides follow the rules (every panel and control
// has a name and both texts, no empty or duplicate texts, sane positions, known variants, translations that fit), the
// tree joins the guide, the game's controls, the catalog's items and the grips, the manual pages take the guide's names,
// and the Cockpits page selects and marks.
[SupportedOSPlatform("windows")]
public sealed class CockpitGuideTests
{
    private static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ShippedGuides => CockpitGuideLibrary.FolderOf(CockpitDatabaseTests.ShippedCockpits);

    // ------------------------------------------------------------------ the shipped data

    [Fact]
    public void ShippedGuides_FollowTheRules()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        CockpitGuideLibrary lib = CockpitGuideLibrary.Load(ShippedGuides);
        Assert.NotEmpty(lib.Guides);
        List<string> problems = lib.Check(catalog);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
        foreach (CockpitGuide g in lib.Guides.Values)
        {
            CockpitGuide r = lib.Resolve(g.Id)!;
            List<CockpitGuideControl> controls = r.AllControls().Select(t => t.Item3).ToList();
            Assert.True(controls.Count > 0, g.Id + " has no controls");
            // Every control: a name and both texts; positions named once, each with its text.
            Assert.All(controls, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Name), g.Id + "/" + c.Id);
                Assert.True(c.Text.Trim().Length >= 40, g.Id + "/" + c.Id + ": text");
                Assert.True(c.Context.Trim().Length >= 30, g.Id + "/" + c.Id + ": context");
                Assert.Equal(c.Positions.Count, c.Positions.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
                Assert.All(c.Positions, p => Assert.False(string.IsNullOrWhiteSpace(p.Text), g.Id + "/" + c.Id + " " + p.Name));
            });
            foreach (string v in g.Variants)
                Assert.NotNull(catalog.Variant(v));
        }
    }

    [Fact]
    public void ShippedGuides_ExplainEveryHotasControlOfTheirVariants()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        CockpitGuideLibrary lib = CockpitGuideLibrary.Load(ShippedGuides);
        foreach (CockpitGuide g in lib.Guides.Values.Where(x => !x.Common))
        {
            foreach (string id in g.Variants)
            {
                CockpitVariant v = catalog.Resolve(catalog.Variant(id)!);
                CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, null, lib);
                Assert.Equal(g.Id, tree.GuideId);
                Assert.True(tree.Controls > 0);
                // No pictures: the guide's own controls and the catalog's; the grips are in the guide (control: keys).
                List<string> grips = tree.All().Where(n => n.Key.StartsWith("grip:", StringComparison.Ordinal)).Select(n => n.Name).ToList();
                Assert.True(grips.Count == 0, g.Id + "/" + id + ": grip controls without a guide entry: " + string.Join(", ", grips));
            }
        }
    }

    // ------------------------------------------------------------------ a synthetic cockpit

    private const string CatalogJson = """
        { "format": "joypro-cockpits", "version": 2, "aircraft": [ {
          "id": "test-jet", "name": "Test Jet", "category": "fixed-wing", "role": "fighter", "era": "modern", "nations": ["US"],
          "variants": [
            { "id": "test-jet-a", "name": "Test Jet A", "games": [ { "game": "DCS", "plane": "TEST_JET" } ], "confidence": "low",
              "sources": [ { "id": "m1", "title": "Test manual", "kind": "game-manual" } ],
              "controls": [
                { "id": "stick.trigger", "device": "stick", "name": "Trigger", "type": "trigger", "functions": [ { "dir": "", "text": "Fire the gun" } ] },
                { "id": "throttle.axis.main", "device": "throttle", "name": "Throttle", "type": "axis", "functions": [ { "dir": "axis", "text": "Engine power" } ] } ],
              "panels": [
                { "id": "elec-panel", "name": "Electrical panel", "area": "left-console", "text": "Power.",
                  "items": [ { "n": 1, "name": "Battery switch", "type": "switch2", "positions": ["OFF", "ON"], "text": "Battery power." },
                             { "n": 2, "name": "Volt meter", "type": "gauge", "text": "Shows the voltage." } ] } ] },
            { "id": "test-jet-b", "name": "Test Jet B (back seat)", "seat": "WSO", "seatOf": "test-jet-a", "games": [ { "game": "DCS", "plane": "TEST_JET" } ], "confidence": "low",
              "sources": [ { "id": "m1", "title": "Test manual", "kind": "game-manual" } ],
              "controls": [ { "id": "stick.trigger", "device": "stick", "name": "Trigger", "type": "trigger", "functions": [ { "dir": "", "text": "Fire" } ] },
                            { "id": "throttle.axis.main", "device": "throttle", "name": "Throttle", "type": "axis", "functions": [ { "dir": "axis", "text": "Power" } ] } ] } ] } ] }
        """;

    private const string GuideJson = """
        { "format": "joypro-cockpit-guide", "version": 1, "id": "dcs-test-jet", "title": "Test Jet (DCS)",
          "variants": ["test-jet-a", "test-jet-b"], "games": ["DCS"], "sets": ["DCS/TEST_JET"],
          "sources": [ { "id": "m1", "title": "Test manual" } ],
          "intro": "A small test jet with one seat in front and one behind, used only by the tests of JoyPro.",
          "areas": [
            { "id": "left-console", "where": "left-console", "name": "Left console", "text": "Electrics and engine start.",
              "panels": [ { "id": "elec", "name": "ELEC panel (electrical power)", "db": "elec-panel", "src": "m1", "page": "12",
                "text": "This panel controls the plane's electricity: where it comes from and whether it works.",
                "context": "The pilot uses it first when climbing in, before anything else is switched on.",
                "controls": [
                  { "id": "battery", "name": "BATTERY switch", "type": "switch2", "match": ["PNT-BAT-*", "item:elec-panel:1"],
                    "positions": [ { "name": "OFF", "text": "No power from the battery." }, { "name": "ON", "text": "The battery powers the cockpit." } ],
                    "text": "This switch wakes up the plane's battery. Without it nothing in the cockpit has power.",
                    "context": "It is the first switch of the start-up; the engine start needs it." },
                  { "id": "gen", "name": "GEN switch (generator)", "type": "switch2", "match": ["cmd:gen_*"],
                    "positions": [ { "name": "OFF", "text": "The generator is not connected." }, { "name": "ON", "text": "The generator powers the plane." } ],
                    "text": "This switch connects the generator, which makes electricity while the engine runs.",
                    "context": "After the engine start the generator takes over from the battery." } ] } ] },
            { "id": "rear-console", "where": "rear", "seat": 2, "name": "Back seat console", "text": "The back seat's own switches.",
              "panels": [ { "id": "rear-lights", "name": "Back seat lights", "src": "m1",
                "text": "These switches turn the back seat's lights on and off, like the lamps of a room.",
                "context": "Only the person in the back seat uses them, mostly at night.",
                "controls": [
                  { "id": "flood", "name": "FLOOD light switch", "type": "button", "match": ["label:Rear flood light"],
                    "text": "Pressing this button turns the bright light over the back seat on or off.",
                    "context": "It helps the back seat read maps at night." } ] } ] },
            { "id": "hotas", "where": "hotas", "name": "Stick and throttle", "text": "The pilot's hands rest here.",
              "panels": [ { "id": "stick", "name": "Stick",
                "text": "The stick steers the plane: push, pull and tilt it to fly where the pilot wants.",
                "context": "The pilot holds it all the time in flight; its buttons fire and talk.",
                "controls": [
                  { "id": "trigger", "name": "Gun trigger", "type": "trigger", "match": ["control:stick.trigger", "STICK-TRIGGER"],
                    "text": "Pulling this trigger fires the gun, like the trigger of a water pistol.",
                    "context": "The gun only fires when the MASTER ARM switch is on." },
                  { "id": "throttle", "name": "Throttle lever", "type": "axis", "match": ["control:throttle.axis.main"],
                    "text": "Pushing this lever forward makes the engine stronger; pulling it back makes it weaker.",
                    "context": "It works like the gas pedal of a car and is used all the time." } ] } ] } ] }
        """;

    private const string GermanJson = """
        { "format": "joypro-cockpit-guide-text", "version": 1, "guide": "dcs-test-jet", "language": "de",
          "areas": { "left-console": { "name": "Linke Konsole", "text": "Strom und Triebwerksstart." } },
          "panels": { "elec": { "name": "ELEC-Bedienfeld (Stromversorgung)", "text": "Dieses Feld steuert den Strom des Flugzeugs.", "context": "Der Pilot benutzt es zuerst." } },
          "controls": { "elec/battery": { "name": "BATTERY-Schalter", "text": "Dieser Schalter weckt die Batterie des Flugzeugs auf.", "context": "Er ist der erste Schalter beim Anlassen.",
                                         "positions": { "ON": "Die Batterie versorgt das Cockpit." } } } }
        """;

    private const string CommonJson = """
        { "format": "joypro-cockpit-guide", "version": 1, "id": "dcs-common-test", "common": true, "games": ["DCS"],
          "sources": [ { "id": "g", "title": "Game files" } ],
          "areas": [ { "id": "lights", "where": "overhead", "name": "Lights", "text": "The lights of the cockpit.",
            "panels": [ { "id": "common-lights", "name": "Light switches",
              "text": "These switches turn the outside and inside lights of the plane on and off.",
              "context": "The pilot sets them before taxiing and at night.",
              "controls": [
                { "id": "nav", "name": "NAV lights switch", "type": "switch", "match": ["PNT-NAV-*"],
                  "text": "This switch turns on the coloured lights at the wing tips, so others can see the plane.",
                  "context": "They are on whenever the plane moves at night." },
                { "id": "strobe", "name": "STROBE switch", "type": "switch", "match": ["PNT-STROBE"],
                  "text": "This switch turns on the flashing white lights that make the plane easy to see.",
                  "context": "They are switched on before take-off." } ] } ] } ] }
        """;

    private const string PicturesJson = """
        { "format": "joypro-cockpit-pictures", "version": 1, "game": "DCS", "plane": "TEST_JET", "planes": ["TEST_JET"], "built": "2026-10-18 10:00",
          "controls": [
            { "id": "PNT-BAT-1", "label": "Battery Switch, ON/OFF", "kind": "switch-2", "seat": 1, "animation": { "source": "draw argument", "positions": [0, 1] },
              "views": [ { "view": "panel:left-console-1", "n": 1, "x": 0.2, "y": 0.2 } ] },
            { "id": "PNT-MYSTERY", "label": "Mystery knob", "kind": "rotary", "seat": 1, "views": [ { "view": "panel:left-console-1", "n": 4, "x": 0.8, "y": 0.2 } ] },
            { "id": "PNT-GEAR-LEVER", "label": "Landing gear lever", "kind": "switch-3", "panel": "Gear panel", "seat": 1,
              "animation": { "source": "draw argument", "min": 0, "max": 1, "positions": [0, 0.5, 1] },
              "views": [ { "view": "panel:left-console-1", "x": 0.5, "y": 0.6 } ] },
            { "id": "PNT-STROBE", "label": "Strobe", "kind": "switch-2", "seat": 1, "commands": [ "strobe_toggle" ], "views": [ { "view": "panel:left-console-1", "x": 0.9, "y": 0.9 } ] },
            { "id": "grip:stick", "label": "Stick", "kind": "grip", "seat": 1, "views": [] } ],
          "views": [
            { "id": "overview", "kind": "overview", "title": "Cockpit overview", "seat": 1, "image": "none.png", "width": 100, "height": 100,
              "callouts": [ { "n": 1, "id": "panel:left-console-1", "label": "Left Console", "x": 0.2, "y": 0.6, "link": "panel:left-console-1" } ] },
            { "id": "panel:left-console-1", "kind": "panel", "title": "Left Console", "area": "left-console", "seat": 1, "image": "none.png", "width": 100, "height": 100,
              "callouts": [
                { "n": 1, "id": "PNT-BAT-1", "label": "Battery Switch, ON/OFF", "x": 0.2, "y": 0.2, "kind": "switch" },
                { "n": 2, "id": "PNT-GEN", "label": "Generator", "x": 0.4, "y": 0.2, "kind": "switch", "commands": [ { "name": "Generator ON", "category": "Left Console; ELEC", "id": "gen_on" } ] },
                { "n": 3, "id": "PNT-NAV-1", "label": "Navigation lights", "x": 0.6, "y": 0.2, "kind": "switch" },
                { "n": 4, "id": "PNT-MYSTERY", "label": "Mystery knob", "x": 0.8, "y": 0.2, "kind": "knob", "panel": "Left Console" } ] },
            { "id": "panel:rear-1", "kind": "panel", "title": "Rear", "area": "rear", "seat": 2, "image": "none.png", "width": 100, "height": 100,
              "callouts": [ { "n": 1, "id": "m7", "label": "Rear flood light", "x": 0.5, "y": 0.5, "kind": "button" } ] } ] }
        """;

    private static (CockpitCatalog Catalog, CockpitGuideLibrary Library, CockpitPictureSet Pictures) Synthetic()
    {
        string root = Temp("guide");
        File.WriteAllText(Path.Combine(root, "test.json"), CatalogJson);
        string guide = Path.Combine(root, "guide");
        Directory.CreateDirectory(guide);
        File.WriteAllText(Path.Combine(guide, "dcs-test-jet.json"), GuideJson);
        File.WriteAllText(Path.Combine(guide, "dcs-test-jet.de.json"), GermanJson);
        File.WriteAllText(Path.Combine(guide, "dcs-common-test.json"), CommonJson);
        string set = Path.Combine(root, "pictures", "DCS", "TEST_JET");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "pictures.json"), PicturesJson);
        CockpitCatalog catalog = CockpitCatalog.Load(root);
        Assert.Empty(catalog.Warnings);
        return (catalog, CockpitGuideLibrary.Load(guide), CockpitPictureSet.Load(set)!);
    }

    [Fact]
    public void Tree_JoinsTheGuide_TheGamesControls_TheCatalogItems_AndTheGrips()
    {
        (CockpitCatalog catalog, CockpitGuideLibrary lib, CockpitPictureSet pictures) = Synthetic();
        Assert.Empty(lib.Check(catalog));
        CockpitVariant v = catalog.Resolve(catalog.Variant("test-jet-a")!);
        CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, lib);
        Assert.Equal("dcs-test-jet", tree.GuideId);
        // The guide's areas first (the back seat's area is not in the front seat), then the shared guide's area, then the
        // places of what no guide explains.
        Assert.Equal(["Left console", "Stick and throttle", "Lights"], tree.Roots.Select(r => r.Name).Take(3).ToList());
        Assert.DoesNotContain(tree.All(), n => n.Name == "Back seat console");
        CockpitGuideNode battery = tree.Node("control:elec/battery")!;
        Assert.Equal("BATTERY switch", battery.Name);
        Assert.False(battery.Generic);
        // Matched by the callout id (glob) and by the catalog item.
        Assert.Contains(battery.Hits, h => h.Page == "game:panel:left-console-1" && h.Tag == "click:PNT-BAT-1" && h.Label == "Battery Switch, ON/OFF");
        Assert.Contains(battery.Hits, h => h.Page == "panel:elec-panel" && h.Tag == "item:elec-panel:1");
        // By a command id (cmd: glob), with the command to bind.
        CockpitGuideNode gen = tree.Node("control:elec/gen")!;
        Assert.Equal("Generator ON", gen.Commands.Single().Name);
        // The grips by control: keys.
        Assert.Contains(tree.Node("control:stick/trigger")!.Hits, h => h.Page == "device:stick" && h.Tag == "control:stick.trigger");
        // The shared guide: only its controls this cockpit has (NAV; STROBE only as an animated control below).
        Assert.NotNull(tree.Node("control:common-lights/nav"));
        Assert.True(tree.Node("control:common-lights/nav")!.Shared);
        // A game control no guide explains: under the game's panel name, with the game's label, explained by its kind.
        CockpitGuideNode mystery = tree.Node("game:PNT-MYSTERY")!;
        Assert.True(mystery.Generic);
        Assert.Equal("Mystery knob", mystery.Name);
        Assert.Equal("knob", mystery.Type);
        Assert.Equal("Left console", mystery.Parent!.Parent!.Name);
        // A catalog item no guide control names: in the guide's panel that is that catalog panel.
        CockpitGuideNode volt = tree.Node("item:elec-panel:2")!;
        Assert.Equal("ELEC panel (electrical power)", volt.Parent!.Name);
        Assert.Equal("Shows the voltage.", volt.Note);
        // The model's animated controls that are no callout: listed at their picture, positions from the animation.
        CockpitGuideNode gearLever = tree.Node("game:PNT-GEAR-LEVER")!;
        Assert.Equal("switch3", gearLever.Type);
        Assert.Equal(3, gearLever.Positions.Count);
        Assert.Equal("Gear panel", gearLever.Parent!.Name);
        Assert.Contains(gearLever.Hits, h => h.Page == "game:panel:left-console-1");
        Assert.Equal("knob", mystery.Type);
        Assert.NotNull(tree.Node("control:common-lights/strobe"));
        // Counts, tags, the page titles by the guide's panels and the search.
        Assert.Equal(6, tree.GameControls);
        Assert.Equal(4, tree.GameControlsExplained);
        Assert.Same(battery, tree.NodeOfTag("item:elec-panel:1|fn:x"));
        Assert.Same(battery, tree.ByCallout["panel:left-console-1|PNT-BAT-1"]);
        Assert.StartsWith("ELEC panel (electrical power)", tree.ViewTitles["panel:left-console-1"]);
        Assert.Contains(battery, tree.Search("battery wakes"));
        Assert.Contains(tree.Search("generator"), n => n == gen);
        Assert.Empty(tree.Search("helicopter rotor"));
        Assert.Equal("This switch wakes up the plane's battery.", battery.Short);

        // The back seat: its own area, matched by the label; not the front seat's.
        CockpitGuideTree rear = CockpitGuideTree.Build(catalog, catalog.Resolve(catalog.Variant("test-jet-b")!), pictures, lib);
        Assert.Contains(rear.Roots, r => r.Name == "Back seat console");
        Assert.Contains(rear.Node("control:rear-lights/flood")!.Hits, h => h.Tag == "click:m7");
        Assert.Equal(1, rear.GameControls);

        // German: the translated names and texts, the rest stays English.
        CockpitGuideTree de = CockpitGuideTree.Build(catalog, v, pictures, lib, "de");
        CockpitGuideNode batteryDe = de.Node("control:elec/battery")!;
        Assert.Equal("BATTERY-Schalter", batteryDe.Name);
        Assert.Equal("Die Batterie versorgt das Cockpit.", batteryDe.Positions.Single(p => p.Name == "ON").Text);
        Assert.Equal("No power from the battery.", batteryDe.Positions.Single(p => p.Name == "OFF").Text);
        Assert.Equal("Linke Konsole", de.Roots[0].Name);
        Assert.Equal("GEN switch (generator)", de.Node("control:elec/gen")!.Name);
        // The English guide is not changed by the translation.
        Assert.Equal("BATTERY switch", lib.Resolve("dcs-test-jet")!.Areas[0].Panels[0].Controls[0].Name);
    }

    [Fact]
    public void Tree_WithoutPictures_HasTheGuideAndTheCatalog()
    {
        (CockpitCatalog catalog, CockpitGuideLibrary lib, _) = Synthetic();
        CockpitGuideTree tree = CockpitGuideTree.Build(catalog, catalog.Resolve(catalog.Variant("test-jet-a")!), null, lib);
        Assert.NotNull(tree.Node("control:elec/battery"));
        Assert.Null(tree.Node("control:common-lights/nav"));
        Assert.Equal(0, tree.GameControls);
        // Without a guide: the catalog's panels and grips, explained by their kind.
        CockpitGuideTree bare = CockpitGuideTree.Build(catalog, catalog.Resolve(catalog.Variant("test-jet-a")!), null, new CockpitGuideLibrary());
        Assert.False(bare.HasGuide);
        Assert.Contains(bare.Roots, r => r.Name == "Left console");
        CockpitGuideNode item = bare.Node("item:elec-panel:1")!;
        Assert.True(item.Generic);
        Assert.Equal(["OFF", "ON"], item.Positions.Select(p => p.Name).ToList());
        Assert.Contains(bare.All(), n => n.Key == "grip:stick.trigger" && n.Note.Contains("Fire the gun"));
        Assert.Equal(bare.Controls, bare.All().Count(n => n.Generic));
    }

    // Controls no guide explains get the general explanation of their function (War Thunder, Nuclear Option ...): the
    // longest matching word wins, things that only show are explained as what they are, German where translated.
    [Fact]
    public void Templates_ExplainUnguidedControls_ByTheirFunction()
    {
        (CockpitCatalog catalog, _, CockpitPictureSet pictures) = Synthetic();
        CockpitGuideLibrary shipped = CockpitGuideLibrary.Load(ShippedGuides);
        Assert.Empty(shipped.Check(CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits)).Where(p => p.StartsWith("_templates", StringComparison.Ordinal)));
        CockpitGuideTemplates en = shipped.Templates("en")!;
        Assert.Equal("gear", en.Find("Landing gear lever")!.Id);
        Assert.Equal("radiator", en.Find("Radiator flap")!.Id);
        Assert.Equal("sight", en.Find("Gun sight brightness")!.Id);
        Assert.Null(en.Find("Mystery knob"));
        // A tree without any guide: the catalog's items get the function's text with their own name.
        CockpitVariant v = catalog.Resolve(catalog.Variant("test-jet-a")!);
        CockpitGuideLibrary bare = new() { TemplateFiles = shipped.TemplateFiles };
        CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, bare);
        CockpitGuideNode battery = tree.Node("item:elec-panel:1")!;
        Assert.Equal("battery", battery.Template);
        Assert.True(battery.Generic);
        Assert.Contains("\"Battery switch\"", battery.Text);
        CockpitGuideNode volt = tree.Node("item:elec-panel:2")!;
        Assert.Equal("gauge", volt.Template);
        Assert.Contains(tree.Node("game:PNT-MYSTERY")!.Text, new[] { string.Empty });
        // German.
        CockpitGuideTree de = CockpitGuideTree.Build(catalog, v, pictures, bare, "de");
        Assert.Contains("„Battery switch“", de.Node("item:elec-panel:1")!.Text);
        Assert.Equal("battery", CockpitGuideTemplates.Parse(new Dictionary<string, object> { ["format"] = "joypro-cockpit-guide-templates" }) is { } empty && empty.Items.Count == 0 ? "battery" : "x");
    }

    [Fact]
    public void Check_ReportsBrokenGuides()
    {
        string folder = Temp("guide-check");
        File.WriteAllText(Path.Combine(folder, "bad.json"), """
            { "format": "joypro-cockpit-guide", "version": 1, "id": "Bad Id", "variants": ["no-such-variant"],
              "areas": [ { "id": "a", "name": "A", "text": "",
                "panels": [ { "id": "p", "name": "P", "text": "short", "context": "",
                  "controls": [
                    { "id": "s", "name": "Switch", "type": "switch2", "positions": [ { "name": "ON", "text": "On." }, { "name": "on", "text": "Also on, twice" }, { "name": "OFF", "text": "Off position" } ],
                      "text": "This text is long enough to pass the length rule of the guide check!", "context": "This text is long enough to pass the length rule of the guide check!" },
                    { "id": "s", "name": "Other", "type": "spaceship", "text": "This text is long enough to pass the length rule of the guide check and is a copy.", "context": "Fine context for the second control." },
                    { "id": "t", "name": "Third", "type": "knob", "text": "This text is long enough to pass the length rule of the guide check and is a copy.", "context": "Fine context for the third control." } ] } ] } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "bad.de.json"), """
            { "format": "joypro-cockpit-guide-text", "version": 1, "guide": "Bad Id", "language": "de", "controls": { "p/missing": { "text": "x" } } }
            """);
        CockpitGuideLibrary lib = CockpitGuideLibrary.Load(folder);
        string all = string.Join("\n", lib.Check(CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits)));
        Assert.Contains("id 'Bad Id'", all);
        Assert.Contains("variant 'no-such-variant'", all);
        Assert.Contains("no sources", all);
        Assert.Contains("area a: no text", all);
        Assert.Contains("panel p: text too short", all);
        Assert.Contains("panel p: no context", all);
        Assert.Contains("position 'on' twice", all);
        Assert.Contains("a switch2 has 2 positions, not 3", all);
        Assert.Contains("exclamation mark", all);
        Assert.Contains("text and context are the same", all);
        Assert.Contains("control id twice", all);
        Assert.Contains("unknown type 'spaceship'", all);
        Assert.Contains("has the same text as", all);
        Assert.Contains("control 'p/missing' is not in the guide", all);
    }

    [Fact]
    public void ManualPages_TakeTheGuidesNames_AndItsFirstSentence()
    {
        (CockpitCatalog catalog, CockpitGuideLibrary lib, CockpitPictureSet pictures) = Synthetic();
        CockpitVariant v = catalog.Resolve(catalog.Variant("test-jet-a")!);
        CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, lib);
        List<ManualPage> plain = CockpitManualPages.Build(catalog, v, new ManualPageOptions { Pictures = pictures });
        List<ManualPage> guided = CockpitManualPages.Build(catalog, v, new ManualPageOptions { Pictures = pictures, Guide = tree });
        Assert.Equal("Left Console", plain.Single(p => p.Id == "game:panel:left-console-1").Title);
        ManualPage panel = guided.Single(p => p.Id == "game:panel:left-console-1");
        Assert.StartsWith("ELEC panel (electrical power)", panel.Title);
        Assert.Contains("BATTERY switch", panel.SearchText);
        Assert.Contains("This switch wakes up the plane's battery.", panel.SearchText);
        Assert.DoesNotContain("Without it nothing", panel.SearchText);
        // A control the guide does not explain keeps the game's name.
        Assert.Contains("Mystery knob", panel.SearchText);
        // The overview's callout to that panel is named by the guide's panels.
        Assert.Contains(guided[0].Items, i => i.Lines.Any(l => l.Contains("ELEC panel")));
    }

    // ------------------------------------------------------------------ the Cockpits page

    [Fact]
    public void Page_ShowsTheGuide_PicksAControl_AndMarksItsCallout()
    {
        string root = Temp("guide-page");
        CockpitPictureTests.WriteSyntheticSet(root);
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        try
        {
            CockpitsViewModel vm = new() { PicturesRoot = root };
            vm.Reload();
            Assert.True(vm.Show("f-16", "f-16c-block-50"));
            Assert.True(vm.HasGuideTree);
            Assert.Equal("dcs-f-16c-50", vm.GuideTree!.GuideId);
            Assert.Contains("F-16", vm.GuideIntro);
            // The areas are open, their panels closed.
            Assert.All(vm.GuideRows, r => Assert.True(r.IsArea || r.IsPanel));
            CockpitGuideRow panel = vm.GuideRows.First(r => r.IsPanel);
            vm.ToggleGuideRowCommand.Execute(panel);
            Assert.Contains(vm.GuideRows, r => r.IsControl);
            // The synthetic set's controls are not in the F-16 guide: under their game panel, explained by their kind.
            CockpitGuideNode arm = vm.GuideTree.NodeOfTag("click:PNT_1")!;
            Assert.True(arm.Generic);
            Assert.True(vm.SelectTag("click:PNT_1"));
            Assert.Equal("click:PNT_1", vm.SelectedTag);
            Assert.Equal(arm, vm.SelectedGuideRow!.Node);
            Assert.True(vm.SelectedGuideRow.ShowDetails);
            Assert.Contains("switch", vm.SelectedGuideRow.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("game:panel:", vm.CurrentPage!.Id);
            // A guide control picked in the tree opens its page: the stick page for the trigger.
            CockpitGuideNode trigger = vm.GuideTree.All().First(n => n.Kind == "control" && n.ControlIds.Contains("stick.trigger"));
            Assert.True(vm.ShowNode(trigger));
            Assert.Equal("device:stick", vm.CurrentPage!.Id);
            Assert.Equal("control:stick.trigger", vm.SelectedTag);
            // The search filters the tree (the page search box drives it).
            vm.PageSearchText = "jet fuel starter";
            Assert.Contains(vm.GuideRows, r => r.IsControl && r.Name.Contains("JFS", StringComparison.OrdinalIgnoreCase));
            Assert.True(vm.GuideRows.Count < vm.GuideTree.All().Count());
            Assert.True(vm.HasGuideSearchHits);
            vm.PageSearchText = string.Empty;
            Assert.False(vm.HasGuideSearchHits);
            // Compare with my setup: a game command the profile binds shows its relation.
            CockpitGuideNode withCommand = vm.GuideTree.All().First(n => n.Commands.Count > 0);
            vm.ApplyCompare("{\"Matched\":0,\"Total\":0,\"Functions\":[],\"Commands\":[{\"Game\":\"DCS\",\"Id\":\"" + withCommand.Commands[0].Id + "\",\"Relation\":\"Arm\",\"Input\":\"Button 3\"}]}");
            vm.SelectTag(withCommand.Hits.First(h => h.Tag.Length > 0).Tag);
            Assert.Contains("Arm (Button 3)", vm.SelectedGuideRow!.Relations);
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    // The Cockpits page with the guide open, rendered at 2267x1250 and 1280x800 (dark and light), and the panel page as
    // exported. JOYPRO_GUIDE_RENDER_OUT names the output folder; JOYPRO_GUIDE_PICTURES a pictures root (the picture
    // packs unpacked: <root>\DCS\F-16C_50 ...), else the synthetic set.
    [Fact]
    public void OptIn_Page_RendersWithTheGuide()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GUIDE_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string? pictures = Environment.GetEnvironmentVariable("JOYPRO_GUIDE_PICTURES");
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Temp("guide-render");
            CockpitPictureTests.WriteSyntheticSet(pictures);
        }
        string aircraft = Environment.GetEnvironmentVariable("JOYPRO_GUIDE_AIRCRAFT") ?? "f-16:f-16c-block-50:jfs";
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                foreach (string pick in aircraft.Split(';'))
                {
                    string[] parts = pick.Split(':');
                    foreach (string lang in new[] { "en", "de" })
                    {
                        Loc.SetLanguage(lang);
                        CockpitsViewModel vm = new() { PicturesRoot = pictures };
                        vm.Reload();
                        Assert.True(vm.Show(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
                        string word = parts.Length > 2 ? parts[2] : string.Empty;
                        CockpitGuideNode? node = word.Length == 0 ? null : vm.GuideTree?.Search(word).OrderBy(n => n.Hits.Any(h => h.Tag.StartsWith("click:", StringComparison.Ordinal)) ? 0 : 1).FirstOrDefault(n => n.Kind == "control" && n.HasPicture);
                        if (node != null)
                        {
                            vm.SelectTag(node.Hits.First(h => h.Tag.Length > 0).Tag);
                            vm.ShowNode(node);
                        }
                        foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                        {
                            if (lang == "de" && theme == "light")
                                continue;
                            Application.Current!.RequestedThemeVariant = variant;
                            foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                            {
                                string file = Path.Combine(outDir, "guide-" + parts[0] + "-" + lang + "-" + theme + "-" + w + "x" + h + ".png");
                                Render(new CockpitsView { DataContext = vm }, w, h, file, theme == "dark", node != null);
                            }
                        }
                        if (vm.CurrentPage != null && lang == "en")
                            JoyPro.Gui.Views.Controls.ManualPageView.SavePng(vm.CurrentPage, Path.Combine(outDir, "guide-" + parts[0] + "-page.png"), 1.0, DrawingStyle.Default, false);
                    }
                }
                Loc.SetLanguage("en");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            });
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    // The page scrolled so that the guide and the page are in view (the variant's header is above them).
    private static void Render(CockpitsView view, int width, int height, string file, bool dark, bool scrollToGuide)
    {
        Size size = new(width, height);
        Border page = new() { Background = new SolidColorBrush(dark ? Color.Parse("#202020") : Color.Parse("#F3F3F3")), Child = view };
        Window host = new() { Content = page, Width = width, Height = height };
        for (int pass = 0; pass < 6; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
            if (scrollToGuide && view.FindControl<Grid>("GuideSplit") is { } split && view.GetVisualDescendantsOfType<ScrollViewer>().FirstOrDefault() is { } scroller)
            {
                Point? at = split.TranslatePoint(new Point(0, 0), scroller);
                if (at is { } p && p.Y > 0)
                    scroller.Offset = new Vector(0, scroller.Offset.Y + p.Y - 10);
            }
        }
        // The tree's scroll to the picked row lands in the passes after it is asked for.
        for (int pass = 0; pass < 3; pass++)
        {
            page.InvalidateMeasure();
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(page);
        bitmap.Save(file);
        if (view.FindControl<ListBox>("GuideList") is { } list)
        {
            ScrollViewer? inner = list.GetVisualDescendantsOfType<ScrollViewer>().FirstOrDefault();
            File.WriteAllText(file + ".txt", "selected: " + ((view.DataContext as CockpitsViewModel)?.SelectedGuideRow?.Name ?? "none") + " | index " +
                ((view.DataContext as CockpitsViewModel)?.GuideRows.IndexOf((view.DataContext as CockpitsViewModel)!.SelectedGuideRow!) ?? -1) + " | offset " + inner?.Offset + " | extent " + inner?.Extent + " | viewport " + inner?.Viewport);
        }
        page.Child = null;
        host.Content = null;
    }
}
