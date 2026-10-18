using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.CockpitPictures;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The cockpit pictures made from the user's games (docs/cockpits.md "Pictures from your games"): the readers (EDM, OBJ8,
// textures), the renderer and the line drawing, the views and callouts, the set on disk, the manual pages and the page.
public sealed class CockpitPictureTests
{
    private static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ------------------------------------------------------------------ textures

    [Fact]
    public void Png_RoundTripsThroughTheWriterAndTheReader()
    {
        byte[] rgba = new byte[4 * 3 * 4];
        for (int i = 0; i < 12; i++)
        {
            rgba[4 * i] = (byte)(i * 20);
            rgba[4 * i + 1] = (byte)(255 - i * 10);
            rgba[4 * i + 2] = 7;
            rgba[4 * i + 3] = (byte)(i % 2 == 0 ? 255 : 128);
        }
        byte[] png = PngWriter.Encode(rgba, 4, 3, 4);
        TextureImage? img = TextureImage.Png(png);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(3, img.Height);
        Assert.Equal(rgba, img.Rgba);
    }

    [Fact]
    public void Dds_DecodesAnUncompressedAndADxt1Picture()
    {
        // A 4x4 DXT1 block: colour 0 pure red (0xF800), colour 1 pure blue (0x001F), every texel index 0 except the last (1).
        byte[] dds = DdsHeader(4, 4, "DXT1", 8);
        byte[] block = { 0x00, 0xF8, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x40 };
        TextureImage? img = TextureImage.Decode(dds.Concat(block).ToArray(), "x.dds", 1024);
        Assert.NotNull(img);
        Assert.Equal(255, img!.Rgba[0]);
        Assert.Equal(0, img.Rgba[2]);
        Assert.Equal(255, img.Rgba[4 * 15 + 2]);
        Assert.Equal(0, img.Rgba[4 * 15]);
    }

    private static byte[] DdsHeader(int w, int h, string fourcc, int size)
    {
        byte[] d = new byte[128];
        "DDS "u8.ToArray().CopyTo(d, 0);
        BitConverter.GetBytes(124).CopyTo(d, 4);
        BitConverter.GetBytes(h).CopyTo(d, 12);
        BitConverter.GetBytes(w).CopyTo(d, 16);
        BitConverter.GetBytes(size).CopyTo(d, 20);
        BitConverter.GetBytes(1).CopyTo(d, 28);
        BitConverter.GetBytes(32).CopyTo(d, 76);
        BitConverter.GetBytes(4).CopyTo(d, 80);
        System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 84);
        return d;
    }

    // ------------------------------------------------------------------ X-Plane objects

    [Fact]
    public void XPlaneObj_ReadsGeometryAnimationsAndTheManipulatorsTooltip()
    {
        string dir = Temp("xpobj");
        string file = Path.Combine(dir, "panel.obj");
        File.WriteAllText(file, string.Join("\n",
            "A", "800", "OBJ", "", "TEXTURE panel.png", "POINT_COUNTS 4 0 0 6",
            "VT 0 0 0 0 0 1 0 0", "VT 1 0 0 0 0 1 1 0", "VT 1 1 0 0 0 1 1 1", "VT 0 1 0 0 0 1 0 1",
            "IDX 0", "IDX 1", "IDX 2", "IDX 0", "IDX 2", "IDX 3",
            "ANIM_begin",
            "ANIM_trans 0 0 0 0 0 1 0 1 sim/some/dataref",
            "ANIM_rotate 0 1 0 0 90 -1 1 sim/other/dataref",
            "ATTR_manip_command_switch_up_down up_down sim/systems/battery_on sim/systems/battery_off Battery - give power to the bus",
            "TRIS 0 6",
            "ANIM_end",
            "ATTR_manip_none",
            "TRIS 0 3"));
        XPlaneObj obj = XPlaneObj.Read(file);
        Assert.Equal("panel.png", obj.Texture);
        Assert.Equal(32, obj.Vertices.Length);
        Assert.Equal(2, obj.Runs.Count);
        XPlaneManip m = obj.Runs[0].Manip;
        Assert.Equal("Battery - give power to the bus", m.Tooltip);
        Assert.Equal(["sim/systems/battery_on", "sim/systems/battery_off"], m.Commands);
        // At dataref 0 the rotation is half way (45 degrees) and the translation at 0.
        Vec3 corner = Mat4.Point(obj.Runs[0].Matrix, 1, 0, 0);
        Assert.Equal(Math.Cos(Math.PI / 4), corner.X, 4);
        Assert.Contains("sim/other/dataref", obj.Runs[0].Datarefs);
        Assert.Null(obj.Runs[1].Manip);
    }

    [Fact]
    public void XPlaneManip_SplitsCursorNumbersReferencesAndTooltip()
    {
        XPlaneManip axis = XPlaneManip.Parse("axis_knob", "rotate_large 0.000 1.000 0.025 0.100 sim/cockpit2/switches/panel_brightness_ratio[0] Flood light - pilot".Split(' '));
        Assert.Equal("Flood light - pilot", axis.Tooltip);
        Assert.Equal(["sim/cockpit2/switches/panel_brightness_ratio[0]"], axis.Datarefs);
        XPlaneManip noTip = XPlaneManip.Parse("command", "button sim/operation/toggle_yoke".Split(' '));
        Assert.Equal(string.Empty, noTip.Tooltip);
        Assert.True(noTip.Clickable);
    }

    [Fact]
    public void XPlaneManip_KeepsCapitalisedWordsWithASlashInTheTooltip()
    {
        XPlaneManip m = XPlaneManip.Parse("command_switch_up_down", "up_down sim/electrical/generator_1_on sim/electrical/generator_1_off Generator/Alternator - Charge the battery".Split(' '));
        Assert.Equal("Generator/Alternator - Charge the battery", m.Tooltip);
        Assert.Equal(2, m.Commands.Count);
    }

    [Fact]
    public void Fuzzy_PicksTheSetWithTheTypeNumber_AndNotASpecialVersion()
    {
        Dictionary<string, string> index = new()
        {
            [CockpitPictureSet.Norm("Cessna_172SP")] = "Cessna_172SP",
            [CockpitPictureSet.Norm("Cessna Skyhawk")] = "Cessna_172SP",
            [CockpitPictureSet.Norm("Cessna 172 SP Skyhawk - 180HP")] = "Cessna_172SP",
            [CockpitPictureSet.Norm("Cessna_172SP_G1000")] = "Cessna_172SP_G1000",
            [CockpitPictureSet.Norm("Cessna 172 SP Skyhawk - 180HP - G1000")] = "Cessna_172SP_G1000",
            [CockpitPictureSet.Norm("Cessna_172SP_seaplane")] = "Cessna_172SP_seaplane",
            [CockpitPictureSet.Norm("Cessna 172 SP Skyhawk - 180HP - seaplane")] = "Cessna_172SP_seaplane",
            [CockpitPictureSet.Norm("b738")] = "b738",
            [CockpitPictureSet.Norm("Boeing 737-800")] = "b738"
        };
        Assert.Equal("Cessna_172SP", CockpitPictureSet.Fuzzy(index, ["c172-classic", "Cessna 172 (steam gauges)", "Cessna 172 Skyhawk"]));
        Assert.Equal("Cessna_172SP_G1000", CockpitPictureSet.Fuzzy(index, ["c172-g1000", "Cessna 172 G1000", "Cessna 172 Skyhawk"]));
        Assert.Equal("b738", CockpitPictureSet.Fuzzy(index, ["b737-800", "Boeing 737-800"]));
        Assert.Null(CockpitPictureSet.Fuzzy(index, ["a320neo", "Airbus A320neo"]));
    }

    // ------------------------------------------------------------------ a synthetic cockpit

    // A panel (a flat box) with three switches on it, a stick (a thin box) with two named parts, all in the scene's
    // frame (x forward, y up, z right).
    internal static CockpitScene SyntheticCockpit(out List<CockpitCallout> callouts, out List<CockpitGrip> grips, out Vec3 head)
    {
        CockpitScene scene = new();
        // Painted panels with printed labels and rivets (textures), as a game's cockpit has.
        scene.Materials.Add(new SceneMaterial { Name = "paint", Color = [90, 95, 100], Albedo = PanelTexture(90, 95, 100) });
        scene.Materials.Add(new SceneMaterial { Name = "knob", Color = [220, 220, 210], Albedo = PanelTexture(220, 220, 210) });
        AddBox(scene, 0, 1, new Vec3(0.8, -0.35, -0.3), new Vec3(0.85, 0.0, 0.3));        // front panel
        AddBox(scene, 1, 2, new Vec3(0.78, -0.12, -0.12), new Vec3(0.8, -0.1, -0.1));     // switch 1
        AddBox(scene, 1, 3, new Vec3(0.78, -0.12, 0.1), new Vec3(0.8, -0.1, 0.12));       // switch 2
        AddBox(scene, 1, 4, new Vec3(0.78, -0.25, 0.0), new Vec3(0.8, -0.23, 0.02));      // switch 3
        int stick = scene.Meshes.Count;
        AddBox(scene, 0, 5, new Vec3(0.35, -0.6, -0.02), new Vec3(0.39, -0.3, 0.02));     // stick
        head = new Vec3(0.0, 0.1, 0.0);
        callouts =
        [
            new CockpitCallout { Id = "PNT_1", Label = "MASTER ARM Switch, ARM/SAFE", Panel = "panel:ARMAMENT", PanelTitle = "Armament Panel", Position = new Vec3(0.78, -0.11, -0.11), Kind = "switch",
                Commands = [new CalloutCommand { Name = "Master Arm - ARM", Category = "Front; Armament Panel", Id = "d3001pnilu3001cd5vd1vpnilvunil" }] },
            new CockpitCallout { Id = "PNT_2", Label = "LASER Switch, ARM/OFF", Panel = "panel:ARMAMENT", PanelTitle = "Armament Panel", Position = new Vec3(0.78, -0.11, 0.11), Kind = "switch" },
            new CockpitCallout { Id = "PNT_3", Label = "GEAR Lever", Panel = "panel:ARMAMENT", PanelTitle = "Armament Panel", Position = new Vec3(0.78, -0.24, 0.01), Kind = "lever" }
        ];
        CockpitGrip grip = new() { Device = "stick", Meshes = [stick], Pivot = new Vec3(0.37, -0.45, 0) };
        grip.Parts.Add(new CockpitCallout { Id = "STICK-BTN-TMS", Label = "BTN TMS", Position = new Vec3(0.39, -0.32, 0.0), Kind = "part" });
        grip.Parts.Add(new CockpitCallout { Id = "STICK-BTN-TRIGGER", Label = "BTN TRIGGER", Position = new Vec3(0.39, -0.4, 0.0), Kind = "part" });
        grips = [grip];
        return scene;
    }

    // A 64 x 64 texture: the colour with a grid of light "labels" and dark "rivets".
    internal static TextureImage PanelTexture(byte r, byte g, byte b)
    {
        TextureImage t = new() { Width = 64, Height = 64, Rgba = new byte[64 * 64 * 4] };
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int o = (y * 64 + x) * 4;
                bool label = y % 16 is >= 3 and <= 5 && x % 16 is >= 2 and <= 12 && (x + y) % 3 != 0;
                bool rivet = x % 16 == 14 && y % 16 == 14;
                double k = label ? 1.9 : rivet ? 0.3 : 1.0 + ((x * 7 + y * 13) % 11 - 5) * 0.02;
                t.Rgba[o] = (byte)Math.Min(255, r * k);
                t.Rgba[o + 1] = (byte)Math.Min(255, g * k);
                t.Rgba[o + 2] = (byte)Math.Min(255, b * k);
                t.Rgba[o + 3] = 255;
            }
        }
        t.Mean = [r, g, b, 255];
        return t;
    }

    private static void AddBox(CockpitScene scene, int material, int part, Vec3 a, Vec3 b)
    {
        Vec3 Corner(int i) => new((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        int[][] faces = [[0, 2, 6, 4], [1, 5, 7, 3], [0, 4, 5, 1], [2, 3, 7, 6], [0, 1, 3, 2], [4, 6, 7, 5]];
        Vec3[] normals = [new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1)];
        List<float> p = [];
        List<float> n = [];
        List<float> uv = [];
        List<int> idx = [];
        // Texture coordinates: a 64-pixel texture tile per 10 cm.
        float[][] corners = [[0, 0], [1, 0], [1, 1], [0, 1]];
        for (int f = 0; f < 6; f++)
        {
            int start = p.Count / 3;
            int k = 0;
            foreach (int c in faces[f])
            {
                Vec3 v = Corner(c);
                p.AddRange([(float)v.X, (float)v.Y, (float)v.Z]);
                n.AddRange([(float)normals[f].X, (float)normals[f].Y, (float)normals[f].Z]);
                Vec3 size = b - a;
                double su = Math.Max(Math.Abs(f < 2 ? size.Z : size.X), 0.01) * 10, sv = Math.Max(Math.Abs(f < 4 && f >= 2 ? size.Z : size.Y), 0.01) * 10;
                uv.AddRange([(float)(corners[k][0] * su), (float)(corners[k][1] * sv)]);
                k++;
            }
            idx.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        scene.Meshes.Add(new SceneMesh { Name = "box" + part, Material = material, Part = part, Positions = p.ToArray(), Normals = n.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray() });
    }

    [Fact]
    public void Renderer_DrawsTheModel_AndTheTracerMakesALineDrawingOfIt()
    {
        CockpitScene scene = SyntheticCockpit(out _, out _, out Vec3 head);
        RenderCamera cam = RenderCamera.LookAt(head, new Vec3(0.8, -0.15, 0), 60, 320, 200);
        RenderBuffers b = SoftwareRenderer.Render(scene, cam.Scaled(2), null, null);
        int covered = b.Covered.Count(c => c);
        Assert.True(covered > b.Covered.Length / 10, "the panel is not drawn");
        int centre = (b.Height / 2) * b.Width + b.Width / 2;
        Assert.True(b.Covered[centre]);
        Assert.InRange(b.Depth[centre], 0.6, 1.2);
        LineArtTracer.Result art = LineArtTracer.Trace(b, false, 2, new LineArtTracer.Settings());
        Assert.Equal(320, art.Width);
        Assert.NotEmpty(art.Outline);
        Assert.NotEmpty(art.Fills);
        // The paths are what JoyPro's drawings accept (absolute M L Q C Z).
        DeviceLineArt.ParsePath(art.Outline);
        foreach (KeyValuePair<string, string> f in art.Fills)
            DeviceLineArt.ParsePath(f.Value);
    }

    [Fact]
    public void Views_PlanTheOverviewTheGripAndAFrontalPanelPicture()
    {
        CockpitScene scene = SyntheticCockpit(out List<CockpitCallout> callouts, out List<CockpitGrip> grips, out Vec3 head);
        List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, 400);
        Assert.Equal("overview", views[0].Id);
        Assert.Equal(2, views[0].Callouts.Count); // the panel and the stick
        CockpitViewPlan stick = views.Single(v => v.Kind == "device");
        Assert.Equal("stick", stick.Device);
        Assert.Equal(2, stick.Callouts.Count);
        CockpitViewPlan panel = views.Single(v => v.Kind == "panel");
        Assert.Equal("Armament Panel", panel.Title);
        Assert.True(panel.Camera.Ortho);
        Assert.Equal("front-center", panel.Area);
        // Frontal: looking along +x, numbered top to bottom, left to right.
        Assert.True(panel.Camera.Forward.X > 0.95);
        Assert.Equal(["PNT_1", "PNT_2", "PNT_3"], panel.Callouts.OrderBy(c => c.N).Select(c => c.Id));
        foreach (CockpitCallout c in panel.Callouts)
        {
            Assert.True(panel.Camera.Project(c.Position, out double x, out double y, out _));
            Assert.InRange(x, 0, panel.Camera.Width);
            Assert.InRange(y, 0, panel.Camera.Height);
        }
    }

    // Writes the synthetic cockpit as a DCS set "F-16C_50" under root\DCS.
    internal static string WriteSyntheticSet(string root)
    {
        CockpitScene scene = SyntheticCockpit(out List<CockpitCallout> callouts, out List<CockpitGrip> grips, out Vec3 head);
        List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, 400);
        string folder = Path.Combine(root, "DCS", "F-16C_50");
        PictureWriter.Write(scene, views, folder, new PictureWriter.SetInfo { Game = "DCS", Plane = "F-16C_50", Planes = ["F-16C_50", "F-16C"], Module = "F-16C", Source = "test.edm", GameVersion = "2.9.0" }, null);
        DcsCockpitPictures.WriteIndex(Path.Combine(root, "DCS"), [new DcsCockpitPictures.SetResult { Plane = "F-16C_50", Planes = ["F-16C_50", "F-16C"], Folder = folder }]);
        return folder;
    }

    [Fact]
    public void Set_IsWrittenAndRead_AndFoundForTheVariantByTheGamesAircraftName()
    {
        string root = Temp("pictures");
        string folder = WriteSyntheticSet(root);
        Assert.True(File.Exists(Path.Combine(folder, "pictures.json")));
        Assert.True(File.Exists(Path.Combine(folder, "overview.png")));
        CockpitPictureSet? set = CockpitPictureSet.Load(folder);
        Assert.NotNull(set);
        Assert.Equal("DCS", set!.Game);
        Assert.Equal(3, set.Views.Count);
        CockpitPictureView panel = set.Views.Single(v => v.Kind == "panel");
        Assert.Equal(3, panel.Callouts.Count);
        CockpitPictureCallout arm = panel.Callouts.Single(c => c.Id == "PNT_1");
        Assert.InRange(arm.X, 0, 1);
        Assert.Equal("Master Arm - ARM", arm.Commands.Single().Name);
        Assert.NotNull(set.DrawingOf(panel));
        Assert.True(set.DrawingOf(panel)!.Paths.Count > 0);
        // The overview's callouts lead to the panel and the grip.
        CockpitPictureView overview = set.View("overview")!;
        Assert.Contains(overview.Callouts, c => c.Link == "device:stick");
        Assert.Contains(overview.Callouts, c => c.Link == panel.Id);

        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        CockpitVariant v = catalog.Resolve(catalog.Variant("f-16c-block-50")!);
        CockpitPictureSet? found = CockpitPictureSet.For(v, root);
        Assert.NotNull(found);
        Assert.Equal(folder, found!.Folder);
        Assert.Null(CockpitPictureSet.For(catalog.Resolve(catalog.Variant("a-10c-ii")!), root));
        Assert.Equal("DCS", CockpitPictureSet.Status(root).Single().Game);
    }

    [Fact]
    public void Grip_PartsAreMatchedToTheControlsByTheirWords()
    {
        CockpitPictureView grip = new()
        {
            Kind = "device",
            Device = "stick",
            Callouts =
            [
                new CockpitPictureCallout { Id = "STICK-BTN-TMS", Label = "BTN TMS", X = 0.5, Y = 0.2 },
                new CockpitPictureCallout { Id = "TRTL-R-HAT-SLEW", Label = "R HAT SLEW", X = 0.4, Y = 0.3 },
                new CockpitPictureCallout { Id = "STICK-BTN-TRIGGER", Label = "BTN TRIGGER", X = 0.3, Y = 0.6 }
            ]
        };
        List<CockpitControl> controls =
        [
            new() { Id = "stick.hat.tms", Name = "TMS (Target Management Switch)" },
            new() { Id = "stick.trigger", Name = "Gun trigger" },
            new() { Id = "throttle.ministick", Name = "Slew control" },
            new() { Id = "stick.pinky.lever", Name = "Pinky switch" }
        ];
        Dictionary<string, CockpitPictureCallout> m = CockpitPictureSet.MatchGrip(grip, controls);
        Assert.Equal("STICK-BTN-TMS", m["stick.hat.tms"].Id);
        Assert.Equal("STICK-BTN-TRIGGER", m["stick.trigger"].Id);
        Assert.Equal("TRTL-R-HAT-SLEW", m["throttle.ministick"].Id);
        Assert.False(m.ContainsKey("stick.pinky.lever"));
    }

    [Fact]
    public void ManualPages_ShowTheGamesCockpit_WithItsPanelsAndTheGripsParts()
    {
        string root = Temp("pictures-pages");
        WriteSyntheticSet(root);
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        CockpitVariant v = catalog.Resolve(catalog.Variant("f-16c-block-50")!);
        foreach (string mode in new[] { "picture", "drawing" })
        {
            ManualPageOptions o = new() { Folder = CockpitDatabaseTests.ShippedCockpits, Pictures = CockpitPictureSet.For(v, root), PictureMode = mode };
            List<ManualPage> pages = CockpitManualPages.Build(catalog, v, o);
            ManualPage overview = pages[0];
            ManualPageItem drawing = overview.Items.Single(i => i.Kind == "drawing");
            Assert.Equal(mode == "picture", drawing.Art!.HasPicture);
            Assert.Equal(mode == "drawing", drawing.Art.LineArt != null);
            // The overview's callouts lead to the game panel's page and to the stick page.
            Assert.Contains(overview.Items, i => i.Link.StartsWith("game:panel:", StringComparison.Ordinal));
            Assert.Contains(overview.Items, i => i.Link == "device:stick");
            ManualPage panel = pages.Single(p => p.Id.StartsWith("game:panel:", StringComparison.Ordinal));
            Assert.Equal("Armament Panel", panel.Title);
            Assert.Contains("MASTER ARM Switch, ARM/SAFE", panel.SearchText);
            Assert.Contains("Commands to bind: Master Arm - ARM", panel.SearchText);
            // The stick page shows the game's grip; TMS and the trigger point at their parts, the rest is listed.
            ManualPage stick = pages.Single(p => p.Id == "device:stick");
            Assert.Contains(stick.Items, i => i.Kind == "drawing");
            Assert.Contains(stick.Items, i => i.Kind == "dot" && i.Tag == "control:stick.hat.tms");
            Assert.Contains("Not shown on the drawing", stick.SearchText);
            // The database's own panels follow.
            Assert.Contains(pages, p => p.Id.StartsWith("panel:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Page_OffersToBuildThePictures_AndShowsThemForTheVariant()
    {
        string root = Temp("pictures-page");
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        try
        {
            CockpitsViewModel vm = new() { PicturesRoot = root };
            int builds = 0;
            vm.PictureBuilder = () =>
            {
                builds++;
                WriteSyntheticSet(root);
                return Task.FromResult<string?>("ok");
            };
            vm.Reload();
            Assert.Contains("No pictures from your games yet", vm.PicturesStatus);
            Assert.True(vm.Show("f-16", "f-16c-block-50"));
            Assert.False(vm.HasGamePictures);
            Assert.DoesNotContain(vm.Pages, p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal));
            await vm.BuildPicturesCommand.ExecuteAsync(null);
            Assert.Equal(1, builds);
            Assert.Contains("DCS World 1 cockpit", vm.PicturesStatus);
            Assert.True(vm.HasGamePictures);
            Assert.Contains("DCS World", vm.GamePicturesText);
            Assert.Contains(vm.PanelPages, p => p.Page.Id.StartsWith("game:panel:", StringComparison.Ordinal));
            Assert.True(vm.Pages[0].Page.Items.Single(i => i.Kind == "drawing").Art!.HasPicture);
            vm.PictureModeIndex = CockpitsViewModel.ModeDrawing;
            Assert.NotNull(vm.Pages[0].Page.Items.Single(i => i.Kind == "drawing").Art!.LineArt);
            // A link on the overview opens the game panel's page.
            string link = vm.Pages[0].Page.Items.First(i => i.Link.StartsWith("game:", StringComparison.Ordinal)).Link;
            Assert.True(vm.OpenPage(link));
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    // ------------------------------------------------------------------ the games' own files (maintainer's PC only)

    [Fact]
    public void Edm_ReadsADcsCockpitModel_WhenDcsIsInstalled()
    {
        string file = @"G:\Program Files\Eagle Dynamics\DCS World\Mods\aircraft\F-15C\Cockpit\Shape\Cockpit_F-15C.EDM";
        if (!File.Exists(file))
            return;
        EdmFile edm = EdmFile.Read(file);
        Assert.Equal(10, edm.Version);
        Assert.True(edm.Nodes.Count > 100);
        Assert.True(edm.RenderNodes.Count > 50);
        Assert.Empty(edm.Warnings);
        CockpitScene scene = EdmScene.Build(edm, new EdmScene.Options());
        Assert.True(scene.TriangleCount > 10000);
        Assert.True(scene.Points.Count > 50);
    }
}
