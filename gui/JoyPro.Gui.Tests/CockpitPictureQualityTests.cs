using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.CockpitPictures;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// The quality gate of the cockpit pictures (docs/cockpits.md "Picture quality"), the fixes behind the unusable X-Plane
// renders, the choice of the better set of two games, and the photo downloads at the bottom of the Cockpits page.
public sealed class CockpitPictureQualityTests
{
    private static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Picture(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);
                int o = (y * w + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }
        return rgba;
    }

    // A panel-like picture: grey with light labels and dark knobs on a grid.
    private static byte[] Detailed(int w, int h, int shift = 0) => Picture(w, h, (x, y) =>
    {
        int xx = x + shift;
        bool label = y % 24 is >= 4 and <= 8 && xx % 40 is >= 3 and <= 30 && (xx / 3 + y) % 4 != 0;
        bool knob = (xx % 40 - 20) * (xx % 40 - 20) + (y % 24 - 16) * (y % 24 - 16) < 20;
        byte v = (byte)(label ? 230 : knob ? 20 : 70 + (xx * 7 + y * 3) % 9);
        return (v, v, (byte)(v + 3 > 255 ? 255 : v + 3), 255);
    });

    [Fact]
    public void Quality_RatesFlatBlackEmptyAndDetailedPictures()
    {
        PictureMetrics flat = PictureQuality.Measure(Picture(320, 200, (_, _) => (128, 128, 128, 255)), 320, 200, 4);
        Assert.True(PictureQuality.Rate(flat, "panel") < PictureQuality.Keep);
        Assert.Contains("little detail", flat.Problems);
        Assert.Contains("one flat colour", flat.Problems);

        PictureMetrics black = PictureQuality.Measure(Picture(320, 200, (x, y) => x > 300 && y > 180 ? ((byte)200, (byte)200, (byte)200, (byte)255) : ((byte)2, (byte)2, (byte)2, (byte)255)), 320, 200, 4);
        Assert.True(PictureQuality.Rate(black, "panel") < PictureQuality.Keep);
        Assert.Contains("mostly black", black.Problems);

        PictureMetrics empty = PictureQuality.Measure(new byte[320 * 200 * 4], 320, 200, 4);
        Assert.Equal(0, PictureQuality.Rate(empty, "overview"));
        Assert.Contains("empty", empty.Problems);

        PictureMetrics good = PictureQuality.Measure(Detailed(320, 200), 320, 200, 4);
        Assert.True(PictureQuality.Rate(good, "panel") >= PictureQuality.Good, "score " + good.Score);
        // The same picture without textures (the renderer's share) or with its controls hidden behind something.
        good.Textured = 0.1;
        Assert.Contains("untextured", Rated(good, "panel"));
        good.Textured = 1;
        good.Visible = 0;
        Assert.Contains("controls hidden", Rated(good, "panel"));

        // A grip far too small in its frame (transparent around it is fine for a grip, a speck is not).
        PictureMetrics speck = PictureQuality.Measure(Picture(400, 400, (x, y) => x is > 180 and < 222 && y is > 180 and < 222 ? ((byte)(x * 9), (byte)(y * 7), (byte)90, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)), 400, 400, 4);
        Assert.Contains("too small in the frame", Rated(speck, "device"));
    }

    private static List<string> Rated(PictureMetrics m, string kind)
    {
        PictureQuality.Rate(m, kind);
        return m.Problems;
    }

    [Fact]
    public void Quality_FindsTwoPicturesThatLookTheSame()
    {
        PictureMetrics a = PictureQuality.Measure(Detailed(480, 300), 480, 300, 4);
        PictureMetrics b = PictureQuality.Measure(Detailed(480, 300), 480, 300, 4);
        PictureMetrics other = PictureQuality.Measure(Picture(480, 300, (x, y) => ((byte)(x / 2), (byte)(y / 2), (byte)60, (byte)255)), 480, 300, 4);
        Assert.True(PictureQuality.SameLook(a, b));
        Assert.False(PictureQuality.SameLook(a, other));
        Assert.True(PictureQuality.LowSet(30, [30, 80, 80]));
        Assert.True(PictureQuality.LowSet(90, [90, 20, 20, 20, 20]));
        Assert.False(PictureQuality.LowSet(90, [90, 70, 40]));
    }

    [Fact]
    public void XPlaneObj_StaticOffsetsWithoutADatarefMoveTheParts()
    {
        // The 737's cockpit object places its levers with "ANIM_trans x y z x y z" and "ANIM_rotate x y z r r" (no
        // dataref): left out, every lever and its callout sat at the aircraft's origin.
        string dir = Temp("xpstatic");
        string file = Path.Combine(dir, "levers.obj");
        File.WriteAllText(file, string.Join("\n",
            "A", "800", "OBJ", "", "TEXTURE panel.png", "POINT_COUNTS 3 0 0 3",
            "VT 0 0 0 0 0 1 0 0", "VT 0.1 0 0 0 0 1 1 0", "VT 0 0.1 0 0 0 1 0 1",
            "IDX 0", "IDX 1", "IDX 2",
            "ANIM_begin",
            "ANIM_trans 0.16538 0.29157 1.72625 0.16538 0.29157 1.72625",
            "ANIM_rotate 0 1 0 90 90",
            "ATTR_manip_drag_rotate hand 0 0 0 1 0 0 0 40 0 0 1 0 0 sim/cockpit2/controls/gear_handle_animation none Gear Handle",
            "TRIS 0 3",
            "ANIM_end"));
        XPlaneObj obj = XPlaneObj.Read(file);
        Vec3 origin = Mat4.Point(obj.Runs[0].Matrix, 0, 0, 0);
        Assert.Equal(0.16538, origin.X, 3);
        Assert.Equal(0.29157, origin.Y, 3);
        Assert.Equal(1.72625, origin.Z, 3);
        // Turned 90 degrees about y: +x goes to -z.
        Vec3 tip = Mat4.Point(obj.Runs[0].Matrix, 0.1, 0, 0) - origin;
        Assert.Equal(-0.1, tip.Z, 4);
        Assert.Equal("Gear Handle", obj.Runs[0].Manip.Tooltip);
    }

    [Fact]
    public void XPlane_DdsTexturesAreReadTopDown_LikeTheirPngs()
    {
        // Laminar's DDS files are stored top row first (as any DDS): sampled like a PNG (v = 0 is the bottom row).
        Assert.True(XPlaneCockpitPictures.FlipDds);
    }

    [Fact]
    public void Writer_LeavesOutAPoorPanelPicture_NumbersSameIds_AndRatesTheSet()
    {
        CockpitScene scene = CockpitPictureTests.SyntheticCockpit(out List<CockpitCallout> callouts, out List<CockpitGrip> grips, out Vec3 head);
        // A second panel far behind the pilot, untextured and flat: its picture is poor.
        scene.Materials.Add(new SceneMaterial { Name = "flat", Color = [128, 128, 128] });
        int flatMesh = scene.Meshes.Count;
        scene.Meshes.Add(new SceneMesh
        {
            Name = "wall", Material = scene.Materials.Count - 1, Part = 99,
            Positions = [-1.5f, -1f, 1.5f, -1.5f, 1f, 1.5f, -1.5f, 1f, -1.5f, -1.5f, -1f, -1.5f],
            Normals = [1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0],
            Indices = [0, 1, 2, 0, 2, 3]
        });
        callouts.Add(new CockpitCallout { Id = "PNT_WALL", Label = "Wall switch", Panel = "panel:WALL", PanelTitle = "Wall", Position = new Vec3(-1.5, 0, 0), Kind = "switch" });
        List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, 400);
        Assert.Contains(views, v => v.Title == "Wall");
        Assert.All(views.Where(v => v.Kind == "panel"), v => Assert.NotEmpty(v.Alternatives));
        // Two views with one id (two groups of controls at the same place) get their own files.
        CockpitViewPlan armament = views.First(v => v.Title == "Armament Panel");
        CockpitViewPlan copy = new() { Id = armament.Id, Kind = "panel", Title = "Armament Panel (copy)", Area = armament.Area, Camera = armament.Camera, Meshes = armament.Meshes };
        views.Add(copy);
        string folder = Path.Combine(Temp("gate"), "DCS", "TEST");
        PictureWriter.Write(scene, views, folder, new PictureWriter.SetInfo { Game = "DCS", Plane = "TEST", Planes = ["TEST"] }, null);
        Assert.Equal(armament.Id + "-2", copy.Id);
        CockpitPictureSet? set = CockpitPictureSet.Load(folder);
        Assert.NotNull(set);
        Assert.DoesNotContain(set!.Views, v => v.Title == "Wall");
        Assert.Contains(set.Views, v => v.Title == "Armament Panel");
        // The copy looks like the armament panel: merged into it, not written twice.
        Assert.DoesNotContain(set.Views, v => v.Id == copy.Id);
        Assert.False(File.Exists(Path.Combine(folder, PictureWriter.FileName(copy.Id) + ".png")));
        Assert.All(set.Views, v => Assert.InRange(v.Quality, 0, 100));
        Assert.True(set.QualityScore >= 0);
        Assert.False(set.LowQuality);
        Assert.Contains("\"dropped\":1", File.ReadAllText(Path.Combine(folder, "pictures.json")));
        // The overview's callout to the left-out panel is gone; the one to the armament panel leads to it.
        CockpitPictureView overview = set.View("overview")!;
        Assert.DoesNotContain(overview.Callouts, c => c.Label == "Wall");
        Assert.Contains(overview.Callouts, c => c.Link == armament.Id);
        Assert.Equal(1, PictureWriter.LastVerdict.Dropped);
    }

    // A set on disk as the builders write it, with the quality verdict given.
    private static void FakeSet(string root, string game, string set, string[] planes, bool? low, string source = "")
    {
        string folder = Path.Combine(root, game, set);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "overview.png"), PngWriter.Encode(Detailed(64, 40), 64, 40, 4));
        var manifest = new Dictionary<string, object>
        {
            ["format"] = "joypro-cockpit-pictures", ["version"] = 1, ["generator"] = 2, ["game"] = game, ["plane"] = planes[0], ["planes"] = planes,
            ["source"] = source, ["built"] = "2026-10-18T00:00:00Z",
            ["views"] = new object[] { new Dictionary<string, object> { ["id"] = "overview", ["kind"] = "overview", ["title"] = "Cockpit overview", ["image"] = "overview.png", ["drawing"] = "", ["width"] = 64, ["height"] = 40, ["quality"] = low == true ? 20 : 80, ["drawingReadable"] = false, ["callouts"] = Array.Empty<object>() } }
        };
        if (low != null)
            manifest["quality"] = new Dictionary<string, object> { ["score"] = low == true ? 20 : 80, ["low"] = low.Value };
        File.WriteAllText(Path.Combine(folder, "pictures.json"), JsonSerializer.Serialize(manifest));
        DcsCockpitPictures.WriteIndex(Path.Combine(root, game), [new DcsCockpitPictures.SetResult { Plane = planes[0], Planes = planes.ToList(), Folder = folder }]);
    }

    [Fact]
    public void Set_ALowQualitySetGivesWayToAnotherGamesSet()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        CockpitVariant v = catalog.Resolve(catalog.Variant("b737-800")!);
        string root = Temp("prefer");
        FakeSet(root, "XPlane12", "b738", ["b738", "Boeing 737-800"], low: true);
        FakeSet(root, "MSFS2024", "asobo_b737max", ["737 Max 8", "asobo_b737max"], low: false, source: "screenshot");
        Assert.Equal("MSFS2024", CockpitPictureSet.For(v, root)!.Game);
        // Without a verdict (sets made before the gate) the order of the games decides.
        string old = Temp("prefer-old");
        FakeSet(old, "XPlane12", "b738", ["b738", "Boeing 737-800"], low: null);
        FakeSet(old, "MSFS2024", "asobo_b737max", ["737 Max 8", "asobo_b737max"], low: null, source: "screenshot");
        Assert.Equal("XPlane12", CockpitPictureSet.For(v, old)!.Game);
        // Only low-quality sets: the best there is still shows.
        string only = Temp("prefer-only");
        FakeSet(only, "XPlane12", "b738", ["b738", "Boeing 737-800"], low: true);
        Assert.Equal("XPlane12", CockpitPictureSet.For(v, only)!.Game);
        // The picture packs choose the same way.
        CockpitPicturePackManifest manifest = new()
        {
            PackVersion = 1,
            Packs =
            [
                new CockpitPicturePack("XPlane12/b738", "XPlane12", "b738", ["b738", "Boeing 737-800"], "x.zip", 1, new string('0', 64), 1, "", "") { Low = true },
                new CockpitPicturePack("MSFS2024/asobo_b737max", "MSFS2024", "asobo_b737max", ["737 Max 8"], "m.zip", 1, new string('0', 64), 1, "", "")
            ]
        };
        Assert.Equal("MSFS2024", manifest.For(v)!.Game);
    }

    [Fact]
    public void Index_ASharedNameLeadsToThePlainVersion_NotTheSeaplane()
    {
        // X-Plane's 172, 172 G1000 and 172 seaplane all call themselves "Cessna 172 SP" (and "C172"): the Cockpits page
        // showed the seaplane for the 172.
        string root = Temp("index");
        List<DcsCockpitPictures.SetResult> results = [];
        foreach (string set in new[] { "Cessna_172SP", "Cessna_172SP_seaplane" })
        {
            FakeSet(root, "XPlane12", set, [set, "Cessna 172 SP", "C172"], low: false);
            results.Add(new DcsCockpitPictures.SetResult { Plane = set, Planes = [set, "Cessna 172 SP", "C172"], Folder = Path.Combine(root, "XPlane12", set) });
        }
        DcsCockpitPictures.WriteIndex(Path.Combine(root, "XPlane12"), results);
        string index = File.ReadAllText(Path.Combine(root, "XPlane12", "index.json"));
        Assert.Contains("\"Cessna 172 SP\":\"Cessna_172SP\"", index);
        Assert.Contains("\"C172\":\"Cessna_172SP\"", index);
        Assert.Contains("\"Cessna_172SP_seaplane\":\"Cessna_172SP_seaplane\"", index);
    }

    [Fact]
    public void ManualPage_DrawingModeShowsThePicture_WhereTheDrawingIsUnreadable()
    {
        string root = Temp("unreadable");
        FakeSet(root, "MSFS2024", "asobo_b737max", ["737 Max 8", "asobo_b737max"], low: false, source: "screenshot");
        CockpitPictureSet set = CockpitPictureSet.Load(Path.Combine(root, "MSFS2024", "asobo_b737max"))!;
        CockpitPictureView view = set.View("overview")!;
        Assert.False(view.DrawingReadable);
        CockpitArt? art = CockpitManualPages.GameArt(view, new ManualPageOptions { PictureMode = "drawing" });
        Assert.NotNull(art);
        Assert.True(art!.HasPicture);
    }

    // ------------------------------------------------------------------ the animated controls (pictures.json "controls")

    [Fact]
    public void Controls_EveryControlOnce_WithKindAnimationAndItsPlaceOnThePictures()
    {
        CockpitScene scene = CockpitPictureTests.SyntheticCockpit(out List<CockpitCallout> callouts, out List<CockpitGrip> grips, out Vec3 head);
        // The master arm switch: a DCS two-position toggle (TUMB, argument 106 from 0 to 1 in steps of 1).
        callouts[0].Animation = DcsCockpitPictures.DcsAnimation(new DcsLua.Clickable { Args = ["106"], Classes = ["TUMB"], Values = ["1"], Limits = ["0:1"] });
        callouts[0].GameKind = "TUMB";
        // The gear lever: a three-position lever of a DCS-like model (-1, 0, 1).
        callouts[2].Animation = DcsCockpitPictures.DcsAnimation(new DcsLua.Clickable { Args = ["8"], Classes = ["TUMB"], Values = ["1"], Limits = ["-1:1"] });
        callouts[2].Kind = "switch";
        grips[0].Animation = new ControlAnimation { Source = "model node", Name = "STICK" };
        // A control the pictures have no callout for (the model's other animated parts).
        scene.Controls.Add(new CockpitCallout { Id = "node:canopy", Label = "Canopy handle", Position = new Vec3(0.78, -0.12, -0.11), Kind = "lever", Animation = new ControlAnimation { Source = "animated node", Name = "canopy" } });
        List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, 400);
        string folder = Path.Combine(Temp("controls"), "DCS", "TEST");
        PictureWriter.Write(scene, views, folder, new PictureWriter.SetInfo { Game = "DCS", Plane = "TEST", Planes = ["TEST"] }, null);
        CockpitPictureSet set = CockpitPictureSet.Load(folder)!;
        CockpitPictureControl arm = set.Controls.Single(c => c.Id == "PNT_1");
        Assert.Equal("switch-2", arm.Kind);
        Assert.Equal("TUMB", arm.GameKind);
        Assert.Equal("draw argument", arm.AnimationSource);
        Assert.Equal("106", arm.AnimationName);
        Assert.Equal([0.0, 1.0], arm.Positions);
        Assert.Contains("d3001pnilu3001cd5vd1vpnilvunil", arm.Commands);
        // On its own panel picture with its number, and on the overview where the switch is drawn.
        CockpitPictureView panel = set.Views.Single(v => v.Kind == "panel");
        CockpitPictureSpot own = arm.Views.Single(v => v.View == panel.Id);
        Assert.True(own.N > 0);
        Assert.InRange(own.X, 0, 1);
        Assert.Equal("switch-3", set.Controls.Single(c => c.Id == "PNT_3").Kind);
        Assert.Equal([-1.0, 0.0, 1.0], set.Controls.Single(c => c.Id == "PNT_3").Positions);
        // Each control once; the grip and its parts; the model's other control with its place.
        Assert.Equal(set.Controls.Count, set.Controls.Select(c => c.Id).Distinct().Count());
        CockpitPictureControl stick = set.Controls.Single(c => c.Id == "grip:stick");
        Assert.Equal("grip", stick.Kind);
        Assert.Equal("STICK", stick.AnimationName);
        Assert.Contains(stick.Views, v => v.View == "device:stick");
        Assert.Contains(set.Controls, c => c.Id == "STICK-BTN-TMS" && c.Views.Any(v => v.View == "device:stick" && v.N > 0));
        CockpitPictureControl canopy = set.Controls.Single(c => c.Id == "node:canopy");
        Assert.Equal("lever", canopy.Kind);
        Assert.Contains(canopy.Views, v => v.View == panel.Id && v.N == 0);
    }

    [Fact]
    public void Controls_XPlaneManipulators_GiveTheirDatarefAndRange()
    {
        XPlaneManip knob = XPlaneManip.Parse("axis_knob", "rotate_small 0 100 1 5 sim/cockpit2/switches/instrument_brightness Panel brightness".Split(' '));
        ControlAnimation a = knob.Animation();
        Assert.Equal("dataref", a.Source);
        Assert.Equal("sim/cockpit2/switches/instrument_brightness", a.Name);
        Assert.Equal(0, a.Min);
        Assert.Equal(100, a.Max);
        Assert.Equal("rotary", ControlKinds.Of(new CockpitCallout { Kind = "knob", GameKind = "axis_knob", Animation = a }));
        XPlaneManip toggle = XPlaneManip.Parse("toggle", "hand 1 0 sim/cockpit2/switches/landing_lights_on Landing lights".Split(' '));
        Assert.Equal([0.0, 1.0], toggle.Animation().Positions);
        Assert.Equal("switch-2", ControlKinds.Of(new CockpitCallout { Kind = "switch", GameKind = "toggle", Animation = toggle.Animation() }));
        Assert.Equal("button", ControlKinds.Of(new CockpitCallout { Kind = "button", GameKind = "command" }));
    }

    // ------------------------------------------------------------------ every picture of a set opens

    // What is wrong with a set on disk: a listed picture or mask that is missing or does not open (or has another size
    // than the picture), a listed drawing that is missing, an overview callout that leads to no view.
    internal static List<string> SetProblems(string folder)
    {
        List<string> problems = [];
        CockpitPictureSet? set = CockpitPictureSet.Load(folder);
        if (set == null)
            return ["pictures.json is missing or not a picture set"];
        foreach (CockpitPictureView v in set.Views)
        {
            if (!File.Exists(v.Image))
            {
                problems.Add(v.Id + ": picture missing (" + Path.GetFileName(v.Image) + ")");
                continue;
            }
            try
            {
                // Decoded as the page decodes it (Skia: PNG, or JPEG with its mask PNG).
                using SkiaSharp.SKBitmap? image = SkiaSharp.SKBitmap.Decode(v.Image);
                if (image == null)
                    problems.Add(v.Id + ": picture does not open");
                else if (image.Width < 16 || image.Height < 16)
                    problems.Add(v.Id + ": picture is empty");
                if (image != null && v.Mask.Length > 0)
                {
                    if (!File.Exists(v.Mask))
                        problems.Add(v.Id + ": mask missing");
                    else
                    {
                        using SkiaSharp.SKBitmap? mask = SkiaSharp.SKBitmap.Decode(v.Mask);
                        if (mask == null || mask.Width != image.Width || mask.Height != image.Height)
                            problems.Add(v.Id + ": mask does not open or has another size");
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add(v.Id + ": picture does not open (" + ex.Message + ")");
            }
            if (v.Drawing.Length > 0 && !Directory.Exists(v.Drawing) && !File.Exists(v.Drawing))
                problems.Add(v.Id + ": drawing missing");
            foreach (CockpitPictureCallout c in v.Callouts.Where(c => c.Link.Length > 0))
            {
                if (set.View(c.Link) == null)
                    problems.Add(v.Id + ": callout " + c.N + " leads to no view (" + c.Link + ")");
            }
        }
        return problems;
    }

    [Fact]
    public void Sets_EveryListedPictureExistsAndOpens()
    {
        string root = Temp("set-opens");
        string folder = CockpitPictureTests.WriteSyntheticSet(root);
        Assert.Empty(SetProblems(folder));
        // A picture that went missing is found.
        File.Delete(Path.Combine(folder, "overview.png"));
        Assert.Contains(SetProblems(folder), p => p.StartsWith("overview: picture missing", StringComparison.Ordinal));
    }

    // Opt-in: every set of a pictures root (JOYPRO_CHECK_PICTURE_SETS=<...\GameData\CockpitPictures>, e.g. the
    // maintainer's build before the packs are made, or a user's folder with downloaded packs).
    [Fact]
    public void OptIn_EverySetOfAPicturesRootOpens()
    {
        string? root = Environment.GetEnvironmentVariable("JOYPRO_CHECK_PICTURE_SETS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;
        List<string> problems = [];
        int sets = 0, views = 0;
        foreach (string game in Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            foreach (string folder in Directory.GetDirectories(game).Where(d => File.Exists(Path.Combine(d, "pictures.json"))))
            {
                sets++;
                views += CockpitPictureSet.Load(folder)?.Views.Count ?? 0;
                problems.AddRange(SetProblems(folder).Select(p => Path.GetFileName(game) + "/" + Path.GetFileName(folder) + " " + p));
            }
        }
        Console.WriteLine("Picture sets: " + sets + " sets, " + views + " pictures, " + problems.Count + " problems");
        Assert.True(sets > 0);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(200)));
    }

    // ------------------------------------------------------------------ the photos at the bottom of the page

    private sealed class FakeServer(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        public string UserAgent = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(answer(Interlocked.Increment(ref Calls)));
        }
    }

    private static HttpResponseMessage Image()
    {
        HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new ByteArrayContent(PngWriter.Encode(Detailed(8, 8), 8, 8, 4)) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return r;
    }

    [Fact]
    public async Task Photos_AreLoadedAgainAfterTooManyRequests_WithAnHonestUserAgent()
    {
        // Wikimedia answers 429 ("too many requests", robot policy) when a program fetches several pictures at once:
        // the 747's photos showed 2 of 5. Now: one at a time, again after the wait, never an error page as a picture.
        TimeSpan spacing = DeviceImageCache.RequestSpacing, retry = DeviceImageCache.FirstRetryDelay;
        DeviceImageCache.RequestSpacing = TimeSpan.FromMilliseconds(1);
        DeviceImageCache.FirstRetryDelay = TimeSpan.FromMilliseconds(5);
        try
        {
            FakeServer busy = new(n => n <= 2 ? new HttpResponseMessage((HttpStatusCode)429) : Image());
            DeviceImageCache.TestHandler = busy;
            byte[]? data = await DeviceImageCache.DownloadAsync(new Uri("https://upload.wikimedia.org/wikipedia/commons/a/ab/X.jpg"));
            Assert.NotNull(data);
            Assert.Equal(3, busy.Calls);
            Assert.StartsWith("JoyPro2/", busy.UserAgent);
            Assert.Contains("github.com/Holdi601/JoystickProfiler2", busy.UserAgent);

            FakeServer errorPage = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Error</html>", System.Text.Encoding.UTF8, "text/html") });
            DeviceImageCache.TestHandler = errorPage;
            Assert.Null(await DeviceImageCache.DownloadAsync(new Uri("https://upload.wikimedia.org/wikipedia/commons/b/bc/Y.jpg")));

            FakeServer gone = new(_ => new HttpResponseMessage((HttpStatusCode)429));
            DeviceImageCache.TestHandler = gone;
            Assert.Null(await DeviceImageCache.DownloadAsync(new Uri("https://upload.wikimedia.org/wikipedia/commons/c/cd/Z.jpg")));
            Assert.Equal(DeviceImageCache.MaxTries, gone.Calls);
        }
        finally
        {
            DeviceImageCache.TestHandler = null;
            DeviceImageCache.RequestSpacing = spacing;
            DeviceImageCache.FirstRetryDelay = retry;
        }
    }

    // Opt-in (network): every photo link of the cockpit database opens (Wikimedia's thumbnail or the file itself is an
    // image). JOYPRO_CHECK_COCKPIT_PHOTOS=1; slow on purpose (one request at a time, as the page does).
    [Fact]
    public async Task OptIn_EveryCockpitPhotoLinkOpens()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_CHECK_COCKPIT_PHOTOS") != "1")
            return;
        DeviceImageCache.TestHandler = null;
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        List<string> failed = [];
        int n = 0;
        foreach (CockpitAircraft a in catalog.Aircraft)
        {
            foreach (CockpitPhoto p in a.Photos.Where(p => p.Url.Length > 0))
            {
                n++;
                string? thumb = ViewModels.CockpitsViewModel.CommonsThumbnail(p.Url, 960);
                byte[]? data = thumb == null ? null : await DeviceImageCache.DownloadAsync(new Uri(thumb));
                data ??= await DeviceImageCache.DownloadAsync(new Uri(p.Url));
                if (data == null || data.Length < 1000)
                    failed.Add(a.Id + ": " + p.Url);
            }
        }
        Assert.True(n > 300, "photos: " + n);
        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }
}
