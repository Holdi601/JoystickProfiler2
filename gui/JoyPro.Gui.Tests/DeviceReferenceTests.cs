using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The device reference (docs/device-reference.md): the shipped catalog, identification of devices (incl. grips that
// share a base's USB id), JoyPro's schematic of every model (built and rendered offscreen), the reference view model
// and the marker assistant with a fake input capture. Nothing is downloaded (manufacturer photos come from a fake);
// everything the user would keep goes to the tests' JOYPRO_DATA_DIR.
[SupportedOSPlatform("windows")]
public sealed class DeviceReferenceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "device-reference-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public DeviceReferenceTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => ShippedCatalog;
        DeviceReferenceService.Reload();
        DeviceImageCache.InternetPicturesEnabled = false;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        DeviceImageCache.InternetPicturesEnabled = false;
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // EngineOnly_Runtime\DB\Devices of the source tree.
    public static string ShippedCatalog
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "EngineOnly_Runtime", "DB", "Devices")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "EngineOnly_Runtime", "DB", "Devices");
        }
    }

    // ------------------------------------------------------------------ catalog

    [Fact]
    public void ShippedCatalog_LoadsEveryFile_WithUniqueIdsAndKnownVocabulary()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        Assert.Empty(catalog.Warnings);
        Assert.True(catalog.Devices.Count >= 300, "only " + catalog.Devices.Count + " models");
        Assert.Equal(catalog.Devices.Count, catalog.Devices.Select(d => d.Id.ToLowerInvariant()).Distinct().Count());
        string[] kinds = ["stick", "throttle", "hotas", "base", "pedals", "collective", "yoke", "quadrant", "panel", "mfd", "buttonbox", "controller", "other"];
        foreach (RefDevice device in catalog.Devices)
        {
            Assert.Contains(device.Kind, kinds);
            Assert.True(device.Base.Length == 0 || catalog.Find(device.Base) != null, device.Id + " names an unknown base " + device.Base);
            foreach (RefControl control in device.AllControls)
            {
                Assert.True(control.Input.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(control.Input, @"^JOY_(X|Y|Z|RX|RY|RZ|SLIDER[12]|BTN\d+|BTN_POV\d_(U|UR|R|DR|D|DL|L|UL))$"),
                    device.Id + ": bad input " + control.Input);
            }
            foreach (string vidPid in device.VidPids)
                Assert.Matches("^[0-9A-F]{4}:[0-9A-F]{4}$", vidPid);
        }
        // Manufacturer photos are only offered from the vendor's own hosts.
        foreach (RefDevice device in catalog.Devices.Where(d => DeviceCatalog.OfficialPhoto(d).Length > 0))
        {
            string host = new Uri(device.Image).Host.ToLowerInvariant();
            Assert.Contains(device.OfficialHosts, h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MiniJson_RoundTripsADefinition()
    {
        RefDevice device = new()
        {
            Id = "user-test",
            Name = "Test \"Stick\" \\ 1",
            Kind = "stick",
            Hand = "right",
            VidPids = { "1234:ABCD" },
            Names = { "Test Stick" },
            Controls = { new RefControl { Input = "JOY_BTN1", Control = "stick.trigger.stage1", Label = "Trigger" } },
            Axes = { new RefControl { Input = "JOY_X", Control = "stick.axis.roll", Type = "analog" } },
            Markers = { ["stick.trigger"] = [0.25, 0.5] }
        };
        string json = DeviceReferenceService.DefinitionJson(device);
        RefDevice back = Assert.Single(DeviceCatalog.ParseFile(json, "x.json"));
        Assert.Equal(device.Name, back.Name);
        Assert.Equal("1234:ABCD", back.VidPids.Single());
        Assert.Equal("stick.trigger.stage1", back.Controls.Single().Control);
        Assert.Equal(0.25, back.Markers["stick.trigger"][0], 3);
    }

    // ------------------------------------------------------------------ identification

    [Theory]
    [InlineData("044F:0402", "HOTAS Warthog Joystick", "thrustmaster-warthog-stick", "exact")]
    [InlineData("044F:0404", "HOTAS Warthog Throttle", "thrustmaster-hotas-warthog-throttle", "exact")]
    [InlineData("044F:B10A", "T.16000M", "thrustmaster-t16000m", "exact")]
    [InlineData("044F:B687", "TWCS Throttle", "thrustmaster-twcs-throttle", "exact")]
    [InlineData("06A3:0762", "Saitek X52 Pro Flight Control System", "logitech-x52-pro", "exact")]
    [InlineData("046D:C215", "Logitech Extreme 3D", "logitech-extreme-3d-pro", "exact")]
    [InlineData("231D:0136", "VKBsim STECS Mini", "vkb-stecs-stg-mini-left", "exact")]
    [InlineData("4098:BE04", "WINWING F18 TAKEOFF PANEL", "winwing-f18-takeoff-panel", "exact")]
    [InlineData("10F5:7055", "VelocityOne Flightstick", "turtlebeach-velocityone-flightstick", "exact")]
    [InlineData("068E:00F3", "CH FIGHTERSTICK USB", "ch-fighterstick", "exact")]
    // The product name alone (no USB id known, e.g. a model picked by name).
    [InlineData("", "HOTAS Warthog Joystick", "thrustmaster-warthog-stick", "name")]
    // The USB id alone (a renamed device).
    [InlineData("044F:0402", "My renamed stick", "thrustmaster-warthog-stick", "vidpid")]
    // Two VKB grips share 231D:0125; the product name tells them apart.
    [InlineData("231D:0125", "VKB-Sim Gunfighter Modern Combat Pro Twist", "vkb-gunfighter-mcg-pro", "exact")]
    public void Identify_FindsTheExactModel(string vidPid, string name, string expected, string method)
    {
        DeviceMatch match = DeviceIdentifier.Identify(DeviceCatalog.Load(ShippedCatalog), new DeviceProbe { VidPid = vidPid, ProductName = name });
        Assert.NotNull(match.Model);
        Assert.Equal(expected, match.Model!.Id);
        Assert.Equal(method, match.Method);
    }

    [Fact]
    public void Identify_LeftHandVirpilBase_OnlyOffersLeftGrips()
    {
        DeviceMatch match = DeviceIdentifier.Identify(DeviceCatalog.Load(ShippedCatalog), new DeviceProbe { VidPid = "3344:80CB", ProductName = "L-VPC Stick WarBRD" });
        IEnumerable<RefDevice> offered = match.Model != null ? [match.Model] : match.Choices;
        Assert.NotEmpty(offered);
        Assert.All(offered, g => Assert.NotEqual("right", g.Hand));
    }

    [Fact]
    public void Identify_UnknownDevice_GetsAGenericModelFromItsInputs()
    {
        DeviceProbe probe = new() { VidPid = "1209:F00D", ProductName = "Home Cockpit Box XYZ", Buttons = 24, Hats = 1, Axes = ["JOY_X"] };
        DeviceMatch match = DeviceIdentifier.Identify(DeviceCatalog.Load(ShippedCatalog), probe);
        Assert.Null(match.Model);
        Assert.Empty(match.Choices);
        RefDevice generic = DeviceGeneric.Model(probe, string.Empty);
        Assert.Equal("panel", generic.Kind);
        Assert.Equal(24 + 4, generic.Controls.Count);
        DeviceSchematic schematic = SchematicBuilder.Build(generic, null);
        Assert.Empty(schematic.Problems());
        Assert.Contains(schematic.Markers, m => m.HasInputs && m.Inputs.Any(i => i.Input == "JOY_BTN24"));
    }

    // A base reporting one USB id for several grips: the grip whose inputs match what the device reports is chosen;
    // when two grips fit equally the user is asked (best fit first). Synthetic catalog, so the test does not depend on
    // the shipped data.
    [Fact]
    public void Identify_TellsGripsApartByTheirInputs()
    {
        string folder = Path.Combine(temp, "kb");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "test.json"), """
        {
          "vendor": "Test", "officialHosts": ["example.invalid"],
          "devices": [
            { "id": "t-base", "name": "Test Base", "kind": "base", "identify": { "vidPids": ["AAAA:0002"], "names": ["Test Base"] },
              "axes": [ { "input": "JOY_X", "control": "stick.axis.roll" }, { "input": "JOY_Y", "control": "stick.axis.pitch" } ] },
            { "id": "t-grip-small", "name": "Small Grip", "kind": "stick", "hand": "right", "base": "t-base",
              "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1" }, { "input": "JOY_BTN12", "control": "stick.button.thumb.1" } ] },
            { "id": "t-grip-big", "name": "Big Grip", "kind": "stick", "hand": "right", "base": "t-base",
              "axes": [ { "input": "JOY_RZ", "control": "stick.axis.twist" } ],
              "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1" }, { "input": "JOY_BTN30", "control": "stick.button.thumb.1" },
                            { "input": "JOY_BTN_POV1_U", "control": "stick.hat.trim.up" } ] },
            { "id": "t-grip-big2", "name": "Big Grip Mk2", "kind": "stick", "hand": "right", "base": "t-base",
              "axes": [ { "input": "JOY_RZ", "control": "stick.axis.twist" } ],
              "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1" }, { "input": "JOY_BTN30", "control": "stick.button.thumb.1" },
                            { "input": "JOY_BTN_POV1_U", "control": "stick.hat.trim.up" } ] },
            { "id": "t-grip-unknown", "name": "Unknown Numbers Grip", "kind": "stick", "hand": "right", "base": "t-base",
              "controls": [ { "control": "stick.trigger.stage1" } ] }
          ]
        }
        """);
        DeviceCatalog catalog = DeviceCatalog.Load(folder);

        DeviceMatch small = DeviceIdentifier.Identify(catalog, new DeviceProbe { VidPid = "AAAA:0002", ProductName = "Test Base", Buttons = 12, Hats = 0, Axes = ["JOY_X", "JOY_Y"] });
        Assert.Equal("t-grip-small", small.Model?.Id);
        Assert.Equal("signature", small.Method);
        Assert.Equal("t-base", small.BaseModel?.Id);

        DeviceMatch big = DeviceIdentifier.Identify(catalog, new DeviceProbe { VidPid = "AAAA:0002", ProductName = "Test Base", Buttons = 32, Hats = 1, Axes = ["JOY_X", "JOY_Y", "JOY_RZ"] });
        Assert.Null(big.Model);
        Assert.Equal("choice", big.Method);
        Assert.Equal(["t-grip-big", "t-grip-big2"], big.Choices.Take(2).Select(c => c.Id).OrderBy(i => i).ToArray());

        // Nothing known about its inputs: every grip is offered.
        DeviceMatch unknown = DeviceIdentifier.Identify(catalog, new DeviceProbe { VidPid = "AAAA:0002", ProductName = "Test Base" });
        Assert.Equal(4, unknown.Choices.Count);

        // A different USB id: not this base.
        Assert.False(DeviceIdentifier.Identify(catalog, new DeviceProbe { VidPid = "AAAA:0009", ProductName = "Something else" }).Found);
    }

    // ------------------------------------------------------------------ schematics

    [Fact]
    public void Schematic_EveryModel_BuildsWithAllPartsInsideAndEveryInputOnAMarker()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        List<string> failures = [];
        foreach (RefDevice model in catalog.Devices)
        {
            DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
            failures.AddRange(schematic.Problems().Select(p => model.Id + ": " + p));
            foreach (SchematicMarker marker in schematic.Markers)
            {
                if (marker.X < 0 || marker.X > 1 || marker.Y < 0 || marker.Y > 1 || marker.Size <= 0)
                    failures.Add(model.Id + ": marker " + marker.Id + " outside the picture");
                foreach (SchematicInput input in marker.Inputs)
                {
                    if (input.X < 0 || input.X > 1 || input.Y < 0 || input.Y > 1)
                        failures.Add(model.Id + ": input point " + input.Input + " outside the picture");
                }
            }
            foreach (RefControl control in model.AllControls.Where(c => c.Input.Length > 0))
            {
                if (schematic.MarkerOf(control.Input) == null)
                    failures.Add(model.Id + ": " + control.Input + " has no marker");
            }
            Assert.True(schematic.Width > 0 && schematic.Height > 0);
            Assert.True(schematic.Body[2] > schematic.Body[0] && schematic.Body[3] > schematic.Body[1], model.Id + ": empty body bounds");
            Assert.StartsWith("<svg", schematic.ToSvg(dark: true, badges: true));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
    }

    [Fact]
    public void Schematic_PutsNamedPartsWhereTheyAreOnSuchADevice()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        DeviceSchematic warthog = SchematicBuilder.Build(catalog.Find("thrustmaster-warthog-stick")!, null);
        Assert.Equal("grip.fighter", warthog.Shape);
        SchematicMarker trigger = warthog.Markers.Single(m => m.Id == "stick.trigger");
        SchematicMarker trim = warthog.Markers.Single(m => m.Id == "stick.hat.trim");
        SchematicMarker gimbal = warthog.Markers.Single(m => m.Id == "stick.gimbal");
        Assert.True(trim.Y < trigger.Y && trigger.Y < gimbal.Y, "trim hat on top, trigger below, gimbal at the bottom");
        Assert.True(trigger.X < trim.X, "the trigger is at the front (left for a right-hand grip)");
        // The hat's directions sit on its sides.
        SchematicMarker tms = warthog.Markers.Single(m => m.Id == "stick.hat.tms");
        SchematicInput up = tms.Inputs.Single(i => i.Direction == "up");
        SchematicInput down = tms.Inputs.Single(i => i.Direction == "down");
        Assert.True(up.Y < tms.Y && down.Y > tms.Y);

        // A left-hand grip is mirrored: the trigger is on the right.
        RefDevice left = catalog.Find("thrustmaster-warthog-stick")!.Clone();
        left.Hand = "left";
        DeviceSchematic mirrored = SchematicBuilder.Build(left, null);
        Assert.True(mirrored.Markers.Single(m => m.Id == "stick.trigger").X > mirrored.Markers.Single(m => m.Id == "stick.hat.trim").X);

        // Pedals: toe brakes on the pedals, rudder in the middle.
        DeviceSchematic pedals = SchematicBuilder.Build(new RefDevice
        {
            Id = "p", Name = "P", Kind = "pedals",
            Axes = { new RefControl { Input = "JOY_X", Control = "pedals.axis.brake_left" }, new RefControl { Input = "JOY_Y", Control = "pedals.axis.brake_right" }, new RefControl { Input = "JOY_RZ", Control = "pedals.axis.rudder" } }
        }, null);
        double l = pedals.MarkerOf("JOY_X")!.X, r = pedals.MarkerOf("JOY_Y")!.X, rudder = pedals.MarkerOf("JOY_RZ")!.X;
        Assert.True(l < rudder && rudder < r);

        // Catalog marker positions win.
        RefDevice moved = catalog.Find("thrustmaster-warthog-stick")!.Clone();
        moved.Markers["stick.hat.trim"] = [0.1, 0.2];
        SchematicMarker movedTrim = SchematicBuilder.Build(moved, null).Markers.Single(m => m.Id == "stick.hat.trim");
        Assert.Equal(0.1, movedTrim.X, 3);
        Assert.Equal(0.2, movedTrim.Y, 3);
    }

    // Every model drawn by the control offscreen, dark and light: no exception, a picture with content, and the drawn
    // part under each marker (the pixel at a marker differs from the empty background).
    [Fact]
    public void Schematic_EveryModel_RendersOffscreen()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        AvaloniaRenderThread.Run(() =>
        {
            List<string> failures = [];
            foreach (RefDevice model in catalog.Devices)
            {
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
                    AvaloniaRenderThread.Wait(Done(vm.ShowAsync(model, catalog.BaseOf(model), string.Empty, "test", generic: false)));
                    DeviceMarkerCanvas canvas = PictureCanvasOf(vm);
                    canvas.ShowBadges = false;
                    canvas.ShowBackdrop = false;
                    canvas.PictureBackground = Brushes.Transparent;
                    ThemeVariantScope host = new() { Width = 400, Height = 400, RequestedThemeVariant = theme, Child = canvas };
                    host.Measure(new Size(400, 400));
                    host.Arrange(new Rect(0, 0, 400, 400));
                    using RenderTargetBitmap bitmap = new(new PixelSize(400, 400));
                    bitmap.Render(host);
                    using MemoryStream png = new();
                    bitmap.Save(png);
                    png.Position = 0;
                    using SKBitmap pixels = SKBitmap.Decode(png);
                    Rect rect = canvas.PictureRect();
                    // Every model resolves to a picture: JoyPro's drawing (a part drawn under every marker) or the
                    // outline of its class (no marker at all: no position is guessed).
                    if (vm.PictureKind == "schematic")
                    {
                        int drawn = 0;
                        foreach (ReferenceMarker marker in vm.Markers)
                        {
                            int px = (int)Math.Clamp(rect.X + marker.X * rect.Width, 0, 399), py = (int)Math.Clamp(rect.Y + marker.Y * rect.Height, 0, 399);
                            SKColor at = pixels.GetPixel(px, py);
                            if (at.Alpha > 0)
                                drawn++;
                        }
                        if (vm.Markers.Count > 0 && drawn < vm.Markers.Count)
                            failures.Add(model.Id + " (" + theme + "): " + (vm.Markers.Count - drawn) + " marker(s) on an empty spot");
                    }
                    else if (vm.PictureKind == "class")
                    {
                        if (vm.CanvasMarkers.Count > 0)
                            failures.Add(model.Id + " (" + theme + "): markers on the class outline");
                        int ink = 0;
                        for (int y = 0; y < 400; y += 4)
                            for (int x = 0; x < 400; x += 4)
                                if (pixels.GetPixel(x, y).Alpha > 0)
                                    ink++;
                        if (ink < 200)
                            failures.Add(model.Id + " (" + theme + "): the class outline is (nearly) empty");
                    }
                    else if (vm.PictureKind == "lineart")
                    {
                        // JoyPro's own drawing (shipped, no consent needed): drawn, with JoyPro's positions of its photo
                        // inside the picture.
                        if (vm.CanvasPhoto is not LineArtImage)
                            failures.Add(model.Id + " (" + theme + "): the line drawing is not on the canvas");
                        foreach (ReferenceCanvasMarker marker in vm.CanvasMarkers)
                        {
                            if (marker.X < 0 || marker.Y < 0 || marker.X > vm.PictureWidth || marker.Y > vm.PictureHeight)
                                failures.Add(model.Id + " (" + theme + "): a marker outside the line drawing");
                        }
                        int ink = 0;
                        for (int y = 0; y < 400; y += 4)
                            for (int x = 0; x < 400; x += 4)
                                if (pixels.GetPixel(x, y).Alpha > 0)
                                    ink++;
                        if (ink < 200)
                            failures.Add(model.Id + " (" + theme + "): the line drawing is (nearly) empty");
                    }
                    else
                    {
                        failures.Add(model.Id + ": shows '" + vm.PictureKind + "' without photos");
                    }
                    if (vm.Markers.Count == 0 && model.AllControls.Any())
                        failures.Add(model.Id + ": no parts");
                    if (vm.Legend.Count == 0 && model.AllControls.Any())
                        failures.Add(model.Id + ": no legend");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
        });
    }

    // ------------------------------------------------------------------ view model

    [Fact]
    public void ViewModel_ShowsSchematicFirst_ThenTheVendorPhotoOnlyWhenAllowed()
    {
        AvaloniaRenderThread.Run(() =>
        {
            string photo = WritePhoto(Path.Combine(temp, "vendor.png"));
            int fetches = 0;
            DeviceReferenceViewModel vm = new()
            {
                VendorPhotosEnabled = false,
                FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); }
            };
            // A panel: JoyPro's drawing is recognizable and knows where every part is. When JoyPro ships its own
            // drawing of the panel (with the curated positions of its photo) that comes first; the schematic stays.
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync("winwing-f18-takeoff-panel")));
            if (vm.PictureKind == "lineart")
            {
                Assert.NotEmpty(vm.CanvasMarkers);
                AvaloniaRenderThread.Wait(Done(vm.SelectPictureAsync(vm.Pictures.Single(p => p.Kind == "schematic"))));
            }
            Assert.Equal("schematic", vm.PictureKind);
            Assert.True(vm.ShowsSchematic);
            Assert.Equal(0, fetches);
            Assert.DoesNotContain(vm.Pictures, p => p.Kind == "vendor");
            Assert.NotEmpty(vm.Markers);
            Assert.Equal(vm.Markers.Count, vm.CanvasMarkers.Count);
            Assert.Equal(vm.Markers.Sum(m => Math.Max(1, m.Inputs.Count)), vm.Legend.Count);

            // Switched on: the photo comes at once (downloaded on this PC only; here a fake). JoyPro has no positions
            // for this photo's content, so no marker is guessed: the legend stays, the marker assistant is offered.
            vm.VendorPhotosEnabled = true;
            AvaloniaRenderThread.Pump();
            Assert.Equal(1, fetches);
            Assert.Equal("vendor", vm.PictureKind);
            Assert.NotNull(vm.Photo);
            Assert.Contains("WinWing", vm.PhotoCredit);
            Assert.Empty(vm.CanvasMarkers);
            Assert.Equal(0, vm.KnownMarkerCount);
            Assert.True(vm.HasNoKnownPositions);
            // JoyPro's curated positions belong to the maker's real photo (its SHA-256): this stand-in is "another
            // version" of it, so they are not used and the user is told.
            Assert.Contains("changed this photo", vm.Status);
            Assert.All(vm.Legend, r => Assert.True(r.ViewText is "drawing" or "line drawing, drawing", r.ViewText));

            // A part placed on the photo is kept for exactly this photo content.
            ReferenceMarker jett = vm.Markers.Single(m => m.Id == "panel.button.1");
            vm.MoveMarker(jett, 0.48, 0.43);
            Assert.Single(vm.CanvasMarkers);
            vm.SaveMarkersCommand.Execute(null);
            DeviceReferenceViewModel again = new() { VendorPhotosEnabled = true, FetchVendorPhoto = _ => Task.FromResult<string?>(photo) };
            AvaloniaRenderThread.Wait(Done(again.ShowModelAsync("winwing-f18-takeoff-panel")));
            if (again.PictureKind != "vendor")
                AvaloniaRenderThread.Wait(Done(again.SelectPictureAsync(again.Pictures.First(p => p.Kind == "vendor"))));
            Assert.Equal("vendor", again.PictureKind);
            Assert.Equal(1, again.KnownMarkerCount);
            Assert.Equal(0.48, again.Markers.Single(m => m.Id == "panel.button.1").X, 3);
            // The maker's photo changed: another content, no position from the old one.
            string changed = WritePhoto(Path.Combine(temp, "vendor-new.png"), 0.3);
            DeviceReferenceViewModel third = new() { VendorPhotosEnabled = true, FetchVendorPhoto = _ => Task.FromResult<string?>(changed) };
            AvaloniaRenderThread.Wait(Done(third.ShowModelAsync("winwing-f18-takeoff-panel")));
            if (third.PictureKind != "vendor")
                AvaloniaRenderThread.Wait(Done(third.SelectPictureAsync(third.Pictures.First(p => p.Kind == "vendor"))));
            Assert.Equal("vendor", third.PictureKind);
            Assert.Equal(0, third.KnownMarkerCount);

            vm.VendorPhotosEnabled = false;
            AvaloniaRenderThread.Pump();
            // Back to JoyPro's own pictures: its drawing of the panel, or the schematic.
            Assert.Contains(vm.PictureKind, new[] { "schematic", "lineart" });
        });
    }

    [Fact]
    public void ViewModel_ComplexGripWithoutPhoto_ShowsTheClassOutlineAndNoGuessedMarker()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync("vkb-gunfighter-mcg-pro")));
            // JoyPro's own drawing of the grip comes first when it has one (with the positions of its photo); the class
            // outline is the last picture and guesses no marker.
            if (vm.Pictures.FirstOrDefault(p => p.Kind == "lineart") is { } lineArt)
            {
                Assert.Equal("lineart", vm.PictureKind);
                Assert.Equal(lineArt.Key, vm.LoadedPicture!.Key);
                Assert.NotEmpty(vm.CanvasMarkers);
                AvaloniaRenderThread.Wait(Done(vm.SelectPictureAsync(vm.Pictures.Single(p => p.Kind == "class"))));
            }
            Assert.Equal("class", vm.PictureKind);
            Assert.NotNull(vm.CanvasSchematic);
            Assert.Empty(vm.CanvasSchematic!.Markers);
            Assert.Empty(vm.CanvasMarkers);
            Assert.NotEmpty(vm.Legend);
            Assert.All(vm.Legend, r => Assert.True(r.ViewText is "not placed yet" or "line drawing", r.ViewText));
            Assert.DoesNotContain(vm.Pictures, p => p.Kind == "schematic");
        });
    }

    [Fact]
    public void ViewModel_UserPhotoWins_MarkersAndLegendTextsAreKeptPerPicture()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync("thrustmaster-warthog-stick")));
            ReferenceMarker trim = vm.Markers.Single(m => m.Id == "stick.hat.trim");
            vm.MoveMarker(trim, 0.42, 0.11);
            Assert.True(vm.HasUnsavedChanges);
            vm.SaveMarkersCommand.Execute(null);

            vm.SetBindings(new Dictionary<string, string> { ["JOY_BTN1"] = "Gun Trigger (F-16C)" });
            ReferenceLegendRow trigger = vm.Legend.First(r => r.Input == "JOY_BTN1");
            Assert.Equal("Gun Trigger (F-16C)", trigger.Command);
            trigger.Name = "Gun trigger first stage";
            Assert.True(trigger.IsEdited);

            // Shown again: the saved position and the edited name come back.
            DeviceReferenceViewModel again = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(again.ShowModelAsync("thrustmaster-warthog-stick")));
            Assert.Equal(0.42, again.Markers.Single(m => m.Id == "stick.hat.trim").X, 3);
            Assert.Equal("Gun trigger first stage", again.Legend.First(r => r.Input == "JOY_BTN1").Name);

            // The user's photo is the best picture; its markers start from the mapping, not from the schematic's.
            string photo = WritePhoto(Path.Combine(temp, "mine.png"));
            AvaloniaRenderThread.Wait(Done(again.SetUserPhotoAsync(photo)));
            Assert.Equal("user", again.PictureKind);
            Assert.False(again.Markers.Single(m => m.Id == "stick.hat.trim").IsPlaced);
            DeviceReferenceViewModel third = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(third.ShowModelAsync("thrustmaster-warthog-stick")));
            Assert.Equal("user", third.PictureKind);
            AvaloniaRenderThread.Wait(Done(third.RemoveUserPhotoCommand.ExecuteAsync(null)));
            Assert.Equal("schematic", third.PictureKind);
        });
    }

    [Fact]
    public void ViewModel_BaseWithSeveralGrips_OffersThemAndShowsThePickedOne()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            RefDevice? chosen = null;
            vm.GripChosen += (_, g) => chosen = g;
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(new DeviceProbe { VidPid = "FFFF:2055", ProductName = "Rhino FFB Joystick" }, "Rhino FFB Joystick {00000000-0000-0000-0000-000000000001}")));
            // The base does not say which grip is on it: "Which grip is on your ...?" with the candidates.
            Assert.True(vm.HasQuestion);
            Assert.Equal("grip", vm.Question!.Kind);
            Assert.Contains("Rhino", vm.QuestionTitle);
            Assert.True(vm.QuestionCandidates.Count >= 10);
            Assert.True(vm.QuestionCandidates[0].IsBest);
            Assert.True(vm.HasGripChoices);
            RefDevice grip = vm.GripChoices.First(g => g.Id != vm.Model?.Id && g.Id == "vpforce-rhino-tm-warthog-grip");
            vm.SelectedGrip = grip;
            AvaloniaRenderThread.Pump();
            Assert.Equal(grip.Id, chosen?.Id);
            Assert.Equal(grip.Id, vm.Model?.Id);
            Assert.False(vm.HasQuestion);

            // Picked in the question: the same.
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(new DeviceProbe { VidPid = "FFFF:2055", ProductName = "Rhino FFB Joystick" }, "Rhino FFB Joystick {00000000-0000-0000-0000-000000000001}")));
            ChoiceCandidate f18 = vm.QuestionCandidates.First(c => c.Model.Id == "vpforce-rhino-tm-f18-grip");
            AvaloniaRenderThread.Wait(Done(vm.ChooseCandidateCommand.ExecuteAsync(f18)));
            Assert.Equal("vpforce-rhino-tm-f18-grip", chosen?.Id);
            Assert.Equal("vpforce-rhino-tm-f18-grip", vm.Model?.Id);
            Assert.False(vm.HasQuestion);

            // Remembered: shown with the picked grip, no question.
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(new DeviceProbe { VidPid = "FFFF:2055", ProductName = "Rhino FFB Joystick" }, "Rhino FFB Joystick {00000000-0000-0000-0000-000000000001}", "vpforce-rhino-tm-f18-grip", pickedGrip: true)));
            Assert.False(vm.HasQuestion);
            Assert.Equal("vpforce-rhino-tm-f18-grip", vm.Model?.Id);
        });
    }

    [Fact]
    public void ViewModel_WarBrdWithoutGripName_AsksWithTheMatchingGripFirst_VkbGripNamedByItsBase_DoesNotAsk()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            DeviceProbe warbrd = new() { VidPid = "3344:80CB", ProductName = "L-VPC Stick WarBRD", InstanceName = "L-VPC Stick WarBRD", Buttons = 31, Hats = 0, Axes = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_SLIDER1"] };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(warbrd, "L-VPC Stick WarBRD {00000000-0000-0000-0000-000000000002}")));
            Assert.True(vm.HasQuestion);
            Assert.Equal("grip", vm.Question!.Kind);
            Assert.Equal("virpil-alpha-l-v2", vm.QuestionCandidates[0].Model.Id);
            // Every Virpil grip that fits the base is offered (the VPC grip connector), the right-hand ones later.
            Assert.Contains(vm.QuestionCandidates, c => c.Model.Id == "virpil-alpha-prime-l");
            Assert.True(vm.QuestionCandidates.ToList().FindIndex(c => c.Model.Hand == "right") > vm.QuestionCandidates.ToList().FindIndex(c => c.Model.Id == "virpil-alpha-prime-l"));

            DeviceProbe gunfighter = new() { VidPid = "231D:0125", ProductName = "VKB-Sim Gunfighter Modern Combat Pro Twist", InstanceName = "VKB-Sim Gunfighter Modern Combat Pro Twist", Buttons = 128, Hats = 2 };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(gunfighter, "VKB-Sim Gunfighter Modern Combat Pro Twist {00000000-0000-0000-0000-000000000003}")));
            Assert.False(vm.HasQuestion);
            Assert.Equal("vkb-gunfighter-mcg-pro", vm.Model?.Id);
        });
    }

    [Fact]
    public void ViewModel_KnownDevice_GetsEveryReportedAxis_ButNotItsExtraButtons()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            // The catalog lists the panel's 5 dials; the device also reports Slider 1 and 2 (and 6 more buttons).
            DeviceProbe panel = new() { VidPid = "4098:BE04", ProductName = "WINWING F18 TAKEOFF PANEL", InstanceName = "WINWING F18 TAKEOFF PANEL", Buttons = 38, Hats = 0, Axes = ["JOY_X", "JOY_Y", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"] };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(panel, "WINWING F18 TAKEOFF PANEL {00000000-0000-0000-0000-000000000005}")));
            Assert.Equal("winwing-f18-takeoff-panel", vm.Model?.Id);
            List<string> axes = vm.Model!.Axes.Select(a => a.Input).ToList();
            Assert.Contains("JOY_SLIDER1", axes);
            Assert.Contains("JOY_SLIDER2", axes);
            Assert.Equal(axes.Count, axes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(vm.Model.Controls, c => c.Input == "JOY_BTN38");
            Assert.NotNull(vm.Schematic!.InputOf("JOY_SLIDER2"));

            // A grip on its base: nothing is added twice.
            DeviceProbe gunfighter = new() { VidPid = "231D:0125", ProductName = "VKB-Sim Gunfighter Modern Combat Pro Twist", InstanceName = "VKB-Sim Gunfighter Modern Combat Pro Twist", Buttons = 128, Hats = 2, Axes = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"] };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(gunfighter, "VKB-Sim Gunfighter Modern Combat Pro Twist {00000000-0000-0000-0000-000000000003}")));
            List<string> gripAxes = vm.Model!.Axes.Select(a => a.Input).ToList();
            Assert.Equal(gripAxes.Count, gripAxes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void ViewModel_UnknownDevice_AsksWhatItIs_FindsItInTheCatalog_OrTakesAClass()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            string? said = null;
            vm.DeviceChosen += (_, choice) => said = choice;
            const string device = "Pedal Thing {00000000-0000-0000-0000-000000000004}";
            DeviceProbe probe = new() { VidPid = "1209:ABCD", ProductName = "Pedal Thing Rudder", InstanceName = "Pedal Thing", Buttons = 0, Hats = 0, Axes = ["JOY_X", "JOY_Y", "JOY_RZ"] };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(probe, device)));
            Assert.True(vm.HasQuestion);
            Assert.True(vm.IsUnknownQuestion);
            Assert.True(vm.ShowsFind);
            Assert.Equal("pedals", vm.ClassChoices[0].Class);
            Assert.True(vm.ClassChoices[0].IsGuess);
            Assert.Equal("class", vm.PictureKind);
            Assert.Equal(3, vm.Legend.Count);

            // Found in the catalog by maker and model words.
            vm.FindText = "mfg crosswind";
            Assert.Contains(vm.FindResults, c => c.Model.Id == "mfg-crosswind-v3");
            AvaloniaRenderThread.Wait(Done(vm.ChooseCandidateCommand.ExecuteAsync(vm.FindResults.First(c => c.Model.Id == "mfg-crosswind-v3"))));
            Assert.Equal("mfg-crosswind-v3", said);
            Assert.Equal("mfg-crosswind-v3", vm.Model?.Id);

            // Or a kind of device: its outline.
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(probe, device)));
            AvaloniaRenderThread.Wait(Done(vm.ChooseClassCommand.ExecuteAsync(vm.ClassChoices.First(c => c.Class == "buttonbox"))));
            Assert.Equal("class:buttonbox", said);
            Assert.Equal("buttonbox", vm.DeviceClass);
            // Remembered as a class: shown so next time without a question.
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(probe, device, "class:buttonbox", pickedGrip: true)));
            Assert.False(vm.HasQuestion);
            Assert.Equal("buttonbox", vm.DeviceClass);
        });
    }

    // ------------------------------------------------------------------ marker assistant

    [Fact]
    public void Assistant_UnknownDevice_PressClickNext_SavesAShareableDefinition()
    {
        AvaloniaRenderThread.Run(() =>
        {
            const string device = "Home Made Box {11111111-0000-0000-0000-000000000001}";
            FakeCapture capture = new();
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false, CaptureService = capture };
            AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(new DeviceProbe { ProductName = "Home Made Box", InstanceName = device, Buttons = 4, Hats = 1, Axes = ["JOY_X"] }, device)));
            Assert.True(vm.IsGeneric);
            vm.OpenAssistantCommand.Execute(null);
            MarkerAssistantViewModel assistant = Assert.IsType<MarkerAssistantViewModel>(vm.Assistant);

            // Button 3: found, then placed where the user clicks.
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = device, Input = "JOY_BTN3" });
            AvaloniaRenderThread.Wait(Done(assistant.StartCommand.ExecuteAsync(null)));
            Assert.Equal("Place", assistant.Step);
            Assert.True(assistant.Current!.HasInput("JOY_BTN3"));
            Assert.Equal(device, capture.Options.Last().PreferredDevice);
            // A press on another device is ignored.
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = "Other {22222222-0000-0000-0000-000000000002}", Input = "JOY_BTN1" });
            // Hat right of the box.
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = device, Input = "JOY_BTN_POV1_R" });
            Assert.True(assistant.PlaceAt(0.3, 0.4));
            ReferenceMarker button3 = vm.MarkerOf("JOY_BTN3")!;
            Assert.Equal(0.3, button3.X, 3);
            Assert.True(button3.IsPlaced);
            AvaloniaRenderThread.Pump();
            Assert.Equal("Place", assistant.Step);
            Assert.True(assistant.Current!.HasInput("JOY_BTN_POV1_R"));
            Assert.True(assistant.Current.HasInput("JOY_BTN_POV1_U"), "the whole hat is one part");
            Assert.True(assistant.PlaceAt(0.6, 0.5));
            AvaloniaRenderThread.Pump();

            // An axis.
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = device, Input = "JOY_X" });
            AvaloniaRenderThread.Wait(Done(assistant.CaptureAxisCommand.ExecuteAsync(null)));
            Assert.True(capture.Options.Last().AxisMode);
            Assert.True(assistant.PlaceAt(0.8, 0.8));
            AvaloniaRenderThread.Pump();
            Assert.Equal(3, assistant.PlacedCount);

            // Undo takes the last one back.
            assistant.UndoCommand.Execute(null);
            Assert.Equal(2, assistant.PlacedCount);
            Assert.False(vm.MarkerOf("JOY_X")!.IsPlaced);

            vm.Legend.First(r => r.Input == "JOY_BTN3").Name = "Master arm";
            assistant.FinishCommand.Execute(null);
            Assert.Equal("Done", assistant.Step);
            string definitionFile = Path.Combine(DeviceCatalog.UserFolder(), "user-home-made-box.json");
            Assert.True(File.Exists(definitionFile), "no definition in " + DeviceCatalog.UserFolder());
            RefDevice saved = Assert.Single(DeviceCatalog.ParseFile(File.ReadAllText(definitionFile), definitionFile));
            Assert.Equal("Master arm", saved.Controls.Single(c => c.Input == "JOY_BTN3").Label);
            Assert.Contains("Home Made Box", saved.Names);

            // The catalog now knows the device by its name; export / import round trip.
            DeviceReferenceService.Reload();
            Assert.Equal("user-home-made-box", DeviceReferenceService.Identify(new DeviceProbe { ProductName = "Home Made Box" }).Model?.Id);
            string export = Path.Combine(temp, "share.json");
            vm.ExportDefinition(export);
            File.Delete(definitionFile);
            DeviceReferenceService.Reload();
            Assert.Equal(1, AvaloniaRenderThread.Wait(vm.ImportDefinitionsAsync(export)));
            Assert.True(File.Exists(definitionFile));
            Assert.Equal("user", DeviceReferenceService.Catalog.Find("user-home-made-box")?.Origin);
        });
    }

    [Fact]
    public void Assistant_KnownModel_FillsAPartWithoutNumberAndAddsUnknownInputs()
    {
        AvaloniaRenderThread.Run(() =>
        {
            const string device = "Joystick - HOTAS Warthog {00000000-0000-0000-0000-000000000009}";
            FakeCapture capture = new();
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false, CaptureService = capture };
            // The TMS hat without its numbers (as a catalog entry of low confidence would have it).
            RefDevice partial = DeviceReferenceService.Catalog.Find("thrustmaster-warthog-stick")!.Clone();
            foreach (RefControl control in partial.Controls.Where(c => c.Control.StartsWith("stick.hat.tms.", StringComparison.Ordinal)))
                control.Input = string.Empty;
            AvaloniaRenderThread.Wait(Done(vm.ShowAsync(partial, null, device, "test", generic: false)));
            RefDevice model = vm.Model!;
            ReferenceMarker? missing = vm.Markers.FirstOrDefault(m => m.Id == "stick.hat.tms");
            Assert.NotNull(missing);
            Assert.All(missing!.Inputs, i => Assert.Empty(i.Input));
            string free = Enumerable.Range(1, 128).Select(n => "JOY_BTN" + n).First(i => !model.AllControls.Any(c => c.Input == i));
            vm.SelectedMarker = missing;
            vm.OpenAssistantCommand.Execute(null);
            MarkerAssistantViewModel assistant = vm.Assistant!;
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = device, Input = free });
            AvaloniaRenderThread.Wait(Done(assistant.StartCommand.ExecuteAsync(null)));
            Assert.Equal(missing!.Id, assistant.Current?.Id);
            Assert.Contains(vm.Model!.AllControls, c => c.Input == free && DeviceVocabulary.ElementOf(c.Control) == missing.Id);
            assistant.SkipCommand.Execute(null);
            AvaloniaRenderThread.Pump();

            // An input nobody knows: a new part.
            vm.SelectedMarker = null;
            string extra = Enumerable.Range(1, 128).Select(n => "JOY_BTN" + n).First(i => !vm.Model!.AllControls.Any(c => c.Input == i));
            capture.Next.Enqueue(new JoystickCaptureService.CaptureResult { Device = device, Input = extra });
            AvaloniaRenderThread.Wait(Done(assistant.StartCommand.ExecuteAsync(null)));
            Assert.NotNull(vm.MarkerOf(extra));
            Assert.True(assistant.PlaceAt(0.5, 0.5));
            AvaloniaRenderThread.Pump();
            assistant.FinishCommand.Execute(null);
            Assert.True(File.Exists(Path.Combine(DeviceCatalog.UserFolder(), "thrustmaster-warthog-stick.json")));
        });
    }

    // ------------------------------------------------------------------ the whole catalog: classes, photos, positions

    // Up to three models of every device class render sensibly: a picture with ink (JoyPro's drawing or the class
    // outline), every shown marker inside the picture, and the class outlines themselves. With
    // JOYPRO_DEVICE_REFERENCE_PREVIEW the pictures are written (class-<class>.png: JoyPro's own drawings only).
    [Fact]
    public void Catalog_ASampleOfEveryClass_RendersSensibly()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_REFERENCE_PREVIEW");
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        AvaloniaRenderThread.Run(() =>
        {
            List<string> failures = [];
            foreach (string cls in DeviceClasses.All)
            {
                DeviceSchematic outline = DeviceClasses.Drawing(cls, "right", null);
                Assert.Empty(outline.Markers);
                Assert.True(outline.Shapes.Count >= 4, cls + ": the outline has too few shapes");
                int ink = InkOf(outline, out SKBitmap pixels);
                if (ink < 300)
                    failures.Add(cls + ": the class outline is nearly empty (" + ink + ")");
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    using SKImage image = SKImage.FromBitmap(pixels);
                    using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(outDir, "class-" + cls + ".png"), png.ToArray());
                }
                pixels.Dispose();
                foreach (RefDevice model in catalog.Devices.Where(d => DeviceClasses.Of(d) == cls).Take(3))
                {
                    DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
                    AvaloniaRenderThread.Wait(Done(vm.ShowAsync(model, catalog.BaseOf(model), string.Empty, "sample", generic: false)));
                    // Without photos: JoyPro's own drawing of the model when it has one, else its schematic or the
                    // class outline.
                    if (vm.PictureKind is not ("schematic" or "class" or "lineart"))
                        failures.Add(model.Id + ": shows " + vm.PictureKind);
                    if (vm.PictureKind == "lineart" ? vm.CanvasPhoto is not LineArtImage : vm.CanvasSchematic == null)
                        failures.Add(model.Id + ": no picture");
                    foreach (ReferenceCanvasMarker marker in vm.CanvasMarkers)
                    {
                        if (marker.X < 0 || marker.Y < 0 || marker.X > vm.PictureWidth || marker.Y > vm.PictureHeight)
                            failures.Add(model.Id + ": a marker outside the picture");
                    }
                    if (vm.PictureKind == "class" && vm.CanvasMarkers.Count > 0)
                        failures.Add(model.Id + ": markers on the class outline");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    private static int InkOf(DeviceSchematic drawing, out SKBitmap pixels)
    {
        DeviceMarkerCanvas canvas = new() { Schematic = drawing, ShowChrome = false, ShowBackdrop = false, PictureBackground = Brushes.Transparent };
        ThemeVariantScope host = new() { Width = 400, Height = 400, RequestedThemeVariant = ThemeVariant.Dark, Child = canvas };
        host.Measure(new Size(400, 400));
        host.Arrange(new Rect(0, 0, 400, 400));
        using RenderTargetBitmap bitmap = new(new PixelSize(400, 400));
        bitmap.Render(host);
        using MemoryStream png = new();
        bitmap.Save(png);
        png.Position = 0;
        pixels = SKBitmap.Decode(png);
        int ink = 0;
        for (int y = 0; y < 400; y += 2)
            for (int x = 0; x < 400; x += 2)
                if (pixels.GetPixel(x, y).Alpha > 0)
                    ink++;
        return ink;
    }

    // The photo database and the curated positions: only addresses on the maker's own hosts, a hash on every curated
    // set that matches the photo reference, only parts the model has. Coverage numbers stay at least where they are.
    [Fact]
    public void Catalog_PhotoReferencesAndCuratedPositions_AreConsistent()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(ShippedCatalog);
        List<string> problems = [];
        foreach (RefDevice model in catalog.Devices)
        {
            foreach (DevicePhoto photo in model.Photos.Where(p => p.View != "main"))
            {
                if (!DevicePhotoDb.IsOfficial(model, photo.Url))
                    problems.Add(model.Id + ": photo not on an official host: " + photo.Url);
            }
        }
        List<DeviceLayoutSet> curated = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(ShippedCatalog), problems);
        foreach (DeviceLayoutSet set in curated)
        {
            RefDevice? model = catalog.Find(set.Model);
            if (model == null)
            {
                problems.Add(set.File + ": unknown model");
                continue;
            }
            DevicePhoto? photo = model.Photos.FirstOrDefault(p => string.Equals(p.Url, set.PhotoUrl, StringComparison.OrdinalIgnoreCase));
            if (photo == null)
                problems.Add(set.File + ": the photo is not referenced by " + model.Id);
            if (set.PhotoSha256.Length != 64 || (photo != null && photo.Sha256.Length > 0 && photo.Sha256 != set.PhotoSha256))
                problems.Add(set.File + ": photo hash missing or different");
            HashSet<string> parts = new(SchematicBuilder.Build(model, catalog.BaseOf(model)).Markers.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            problems.AddRange(set.Markers.Keys.Where(k => !parts.Contains(k)).Select(k => set.File + ": no part " + k));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(catalog.Devices.Count(d => DevicePhotoDb.OfficialPhotos(d).Count > 0) >= 230);
        Assert.True(catalog.Devices.Count(d => DevicePhotoDb.OfficialPhotos(d).Count > 1) >= 50);
        Assert.True(curated.Count >= 2);
    }

    // ------------------------------------------------------------------ preview pictures for the docs

    // Writes docs/device-reference/*.png when JOYPRO_DEVICE_REFERENCE_PREVIEW names the output folder.
    [Fact]
    public void RenderPreviewPictures()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_REFERENCE_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string[] models =
        [
            "thrustmaster-warthog-stick", "thrustmaster-hotas-warthog-throttle", "vkb-gunfighter-mcg-pro", "virpil-alpha-prime-l",
            "vkb-stecs-stg-max-left", "logitech-x52-pro", "logitech-extreme-3d-pro", "honeycomb-alpha-flight-controls",
            "honeycomb-bravo-throttle-quadrant", "mfg-crosswind-v3", "thrustmaster-mfd-cougar-left", "winwing-f18-takeoff-panel",
            "virpil-sharka-50-collective-grip", "moza-ab9"
        ];
        AvaloniaRenderThread.Run(() =>
        {
            DeviceCatalog catalog = DeviceReferenceService.Catalog;
            foreach (string id in models)
            {
                RefDevice? model = catalog.Find(id);
                if (model == null)
                    continue;
                DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
                AvaloniaRenderThread.Wait(Done(vm.ShowAsync(model, catalog.BaseOf(model), string.Empty, "preview", generic: false)));
                RenderCanvas(vm, Path.Combine(outDir, "schematic-" + id + ".png"), ThemeVariant.Dark, 640);
            }

            // The whole control: picture, markers and legend with the binds of a profile (dark and light).
            DeviceReferenceViewModel warthog = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(warthog.ShowDeviceAsync(new DeviceProbe { VidPid = "044F:0402", ProductName = "HOTAS Warthog Joystick" }, "Joystick - HOTAS Warthog {00000000-0000-0000-0000-000000000001}")));
            warthog.SetBindings(new Dictionary<string, string>
            {
                ["JOY_BTN1"] = "Gun Trigger, First Detent",
                ["JOY_BTN2"] = "Weapon Release",
                ["JOY_BTN6"] = "Gun Trigger, Second Detent",
                ["JOY_BTN7"] = "TMS Up",
                ["JOY_BTN9"] = "TMS Down",
                ["JOY_BTN_POV1_U"] = "Trim Nose Down"
            });
            warthog.SelectedMarker = warthog.Markers.FirstOrDefault(m => m.Id == "stick.hat.tms");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                RenderView(warthog, Path.Combine(outDir, "view-warthog-" + (theme == ThemeVariant.Dark ? "dark" : "light") + ".png"), theme, ReferenceLegendPlacement.Right);

            // An unknown device with the marker assistant waiting for a press.
            DeviceReferenceViewModel unknown = new() { VendorPhotosEnabled = false, CaptureService = new FakeCapture() };
            AvaloniaRenderThread.Wait(Done(unknown.ShowDeviceAsync(new DeviceProbe { ProductName = "Home Cockpit Box", Buttons = 12, Hats = 1, Axes = ["JOY_X", "JOY_Y"] }, "Home Cockpit Box {11111111-0000-0000-0000-000000000001}")));
            unknown.OpenAssistantCommand.Execute(null);
            unknown.Assistant!.Found("JOY_BTN5");
            RenderView(unknown, Path.Combine(outDir, "view-assistant-dark.png"), ThemeVariant.Dark, ReferenceLegendPlacement.Bottom);
        });
    }

    private static void RenderCanvas(DeviceReferenceViewModel vm, string file, ThemeVariant theme, int width)
    {
        int height = (int)Math.Ceiling(width / vm.PictureAspect) + 12;
        DeviceMarkerCanvas canvas = PictureCanvasOf(vm);
        canvas.ShowBackdrop = false;
        ThemeVariantScope host = new() { Width = width, Height = height, RequestedThemeVariant = theme, Child = new Border { Background = new SolidColorBrush(Color.Parse(theme == ThemeVariant.Dark ? "#0B1220" : "#EEF2F7")), Child = canvas } };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(host);
        bitmap.Save(file);
    }

    // The picture canvas bound like DeviceReferenceView binds it, without the zoom bar.
    private static DeviceMarkerCanvas PictureCanvasOf(DeviceReferenceViewModel vm) => new()
    {
        Source = vm.CanvasPhoto,
        Schematic = vm.CanvasSchematic,
        SourceWidth = vm.PictureWidth,
        SourceHeight = vm.PictureHeight,
        Markers = vm.CanvasMarkers,
        SelectedMarker = vm.SelectedCanvasMarker,
        Mode = vm.CanvasMode,
        ShowCommands = false,
        ShowChrome = false
    };

    private static void RenderView(DeviceReferenceViewModel vm, string file, ThemeVariant theme, ReferenceLegendPlacement placement)
    {
        Size size = placement == ReferenceLegendPlacement.Right ? new Size(1180, 860) : new Size(900, 1200);
        DeviceReferenceView view = new() { DataContext = vm, LegendPlacement = placement, Margin = new Thickness(16) };
        ThemeVariantScope host = new()
        {
            Width = size.Width,
            Height = size.Height,
            RequestedThemeVariant = theme,
            Child = new Border { Background = new SolidColorBrush(Color.Parse(theme == ThemeVariant.Dark ? "#0B1220" : "#EEF2F7")), Child = view }
        };
        Window window = new() { Content = host, Width = size.Width, Height = size.Height, RequestedThemeVariant = theme };
        window.Measure(size);
        window.Arrange(new Rect(size));
        AvaloniaRenderThread.Pump();
        host.Measure(size);
        host.Arrange(new Rect(size));
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(host);
        bitmap.Save(file);
        window.Content = null;
    }

    // ------------------------------------------------------------------ helpers

    // A "photo": white background with a dark device in the middle (60 % of the width).
    // A stand-in photo: a dark device on white; left = where the device starts (another value = another content).
    private static string WritePhoto(string file, double left = 0.2)
    {
        using SKBitmap bitmap = new(400, 300);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.White);
            using SKPaint paint = new() { Color = new SKColor(30, 40, 60) };
            canvas.DrawRect(new SKRect((float)(400 * left), 30, 320, 270), paint);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(file, data.ToArray());
        return file;
    }

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private sealed class FakeCapture : IJoystickCaptureService
    {
        public Queue<JoystickCaptureService.CaptureResult> Next { get; } = new();
        public List<JoystickCaptureService.CaptureOptions> Options { get; } = [];
        public string LastDiagnostics => string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken)
        {
            Options.Add(options);
            return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);
        }

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
