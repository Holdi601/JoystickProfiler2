using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.DeviceReference;
using Xunit;

namespace JoyPro.Gui.Tests;

// Every input of the devices JoyPro has drawn has its place on the picture (docs/device-reference.md, "Complete
// devices"), and the hats sit where the makers' manuals put them.
[SupportedOSPlatform("windows")]
public sealed class DeviceCompletenessTests
{
    // The drawn devices: the user's devices and the replicas drawn from the games' grips.
    public static readonly string[] Drawn =
    [
        "virpil-mongoost-50cm2-throttle", "virpil-mongoost-50cm-throttle", "virpil-alpha-l-v2", "virpil-warbrd-base",
        "vpforce-rhino", "mfg-crosswind-v2", "thrustmaster-warthog-stick", "thrustmaster-hotas-warthog-throttle", "thrustmaster-f16c-viper-grip",
        "thrustmaster-fa18c-hornet-grip", "thrustmaster-ava-hornet-grip", "vpforce-rhino-tm-f16-grip", "vpforce-rhino-tm-f18-grip", "thrustmaster-warthog-mkii-stick",
        // Wave 3, batch 1 (2026-10-18): composed views and the game-twin grips.
        "thrustmaster-t16000m", "virpil-alpha-prime-r", "virpil-alpha-prime-l", "vkb-gunfighter-scg-premium-left", "vkb-gunfighter-scg-premium-right",
        "winwing-viperace-ex-joystick-grip", "winwing-orion2-joystick-base", "honeycomb-bravo-throttle-quadrant", "virpil-vfx-grip-nt",
        "winwing-navyace-joystick-grip", "winwing-carrierace-joystick-grip", "vkb-gunfighter-f14", "vkb-gnx-evo-f14",
        // Wave 3, batch 2 (2026-10-18).
        "logitech-extreme-3d-pro", "thrustmaster-airbus-add-on-grip", "thrustmaster-ava-sol-r-grip", "thrustmaster-ava-viper-grip",
        "thrustmaster-sol-r-flightstick", "thrustmaster-tca-sidestick-airbus", "thrustmaster-tflight-hotas-one", "thrustmaster-tflight-stick-x",
        "virpil-archer-sf-cyclic-grip", "virpil-warbrd-grip", "vkb-gunfighter-tm-grip", "winwing-3xx-joystick-grip"
    ];

    private static List<DeviceLayoutSet> Sets() => DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog), null);

    [Fact]
    public void DrawnDevices_PlaceEveryInput()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        List<DeviceLayoutSet> sets = Sets();
        List<string> problems = [];
        foreach (string id in Drawn)
        {
            RefDevice model = catalog.Find(id) ?? throw new InvalidOperationException(id);
            DeviceCompleteness c = DeviceCompleteness.Of(model, catalog.BaseOf(model), sets);
            if (!c.Complete)
                problems.Add(id + " (" + c.Picture + "): no place for " + string.Join(", ", c.Missing));
            if (!c.Picture.Contains(".drawing-", StringComparison.Ordinal))
                problems.Add(id + ": the best picture is not JoyPro's drawing but " + c.Picture);
            // Input by input: every button, hat direction and axis the catalog gives the device is in a placed part.
            DeviceLayoutSet set = sets.Single(s => Path.GetFileName(s.File) == c.Picture);
            HashSet<string> placed = new(SchematicBuilder.Build(model, catalog.BaseOf(model)).Markers
                .Where(m => set.Markers.ContainsKey(m.Id) || (set.Unplaced.TryGetValue(m.Id, out string? why) && why.StartsWith(DeviceCompleteness.NotPhysical, StringComparison.Ordinal)))
                .SelectMany(m => m.Inputs).Select(i => i.Input), StringComparer.OrdinalIgnoreCase);
            foreach (string input in model.Axes.Concat(model.Controls).Select(x => x.Input).Where(i => i.Length > 0).Where(i => !placed.Contains(i)))
                problems.Add(id + ": input " + input + " has no place");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // The VKB MCG Pro: VKB publishes no button map; every input read from a real grip has its place except button 4,
    // whose control nobody documents (the Layout page asks the user to press it).
    [Fact]
    public void McgPro_OnlyTheUndocumentedButtonHasNoPlace()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        RefDevice model = catalog.Find("vkb-gunfighter-mcg-pro")!;
        DeviceCompleteness c = DeviceCompleteness.Of(model, catalog.BaseOf(model), Sets());
        Assert.Equal(["stick.button.thumb.4"], c.Missing);
        Assert.Contains(".drawing-", c.Picture, StringComparison.Ordinal);
    }

    // The F/A-18 takeoff panel: five dimmer channels, of which WinWing does not document which knob is which; all
    // other inputs have their place.
    [Fact]
    public void TakeoffPanel_OnlyTheUndocumentedDimmerChannelsHaveNoPlace()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        RefDevice model = catalog.Find("winwing-f18-takeoff-panel")!;
        DeviceCompleteness c = DeviceCompleteness.Of(model, null, Sets());
        Assert.All(c.Missing, m => Assert.StartsWith("panel.axis.", m, StringComparison.Ordinal));
    }

    // The Warthog stick as Thrustmaster's manual shows it (MKII manual p.7, from behind): weapon release top left, trim
    // top right, TMS left below it, DMS right below the trim, CMS on the thumb side below, MMCB on the right side.
    [Fact]
    public void WarthogStick_HatsWhereTheManualPutsThem()
    {
        DeviceLayoutSet set = Sets().Single(s => s.Model == "thrustmaster-warthog-stick" && s.PhotoKind == "lineart");
        double[] trim = set.Markers["stick.hat.trim"], weapon = set.Markers["stick.weapon_release"], tms = set.Markers["stick.hat.tms"],
            dms = set.Markers["stick.hat.dms"], cms = set.Markers["stick.hat.cms"], mmcb = set.Markers["stick.button.index_lower"];
        Assert.True(weapon[0] < trim[0] && Math.Abs(weapon[1] - trim[1]) < 0.06);
        Assert.True(tms[0] < dms[0] && Math.Abs(tms[1] - dms[1]) < 0.04);
        Assert.True(tms[1] > weapon[1] && dms[1] > trim[1]);
        Assert.True(cms[1] > tms[1] + 0.1 && cms[0] < tms[0]);
        Assert.True(mmcb[0] > dms[0]);
        // The front of the grip (manual p.8, seen from the left): the trigger under the head, the NWS button low on the
        // front, the paddle in front of it; both on the side view, below the trigger.
        double[] trigger = set.Markers["stick.trigger"], nws = set.Markers["stick.pinky.button"], paddle = set.Markers["stick.pinky.lever"];
        Assert.True(trigger[0] > 0.5 && nws[0] > 0.5 && paddle[0] > 0.5);
        Assert.True(nws[1] > trigger[1] + 0.2 && paddle[0] < nws[0]);
    }

    // A part listed as not physical counts as done; a part without a place and without that reason does not.
    [Fact]
    public void Completeness_CountsNotPhysicalParts_AndPrefersJoyProsDrawing()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        RefDevice model = catalog.Find("thrustmaster-hotas-warthog-throttle")!;
        DeviceCompleteness c = DeviceCompleteness.Of(model, null, Sets());
        Assert.True(c.Complete, string.Join(", ", c.Missing));
        Assert.Contains("throttle.detent.idle", c.Virtual);
        DeviceLayoutSet only = new() { Model = model.Id, File = "x.json" };
        only.Markers["throttle.hat.mic"] = [0.5, 0.5];
        DeviceCompleteness partial = DeviceCompleteness.Of(model, null, [only]);
        Assert.False(partial.Complete);
        Assert.Equal(1, partial.Placed);
        Assert.Contains("throttle.detent.idle", partial.Missing);
    }
}
