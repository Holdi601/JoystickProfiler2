using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.DeviceReference;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// JoyPro's own drawings of the devices (EngineOnly_Runtime\DB\DeviceDrawings, DB\Cockpits\line-drawings;
// docs/device-reference.md "JoyPro's drawings"): every shipped drawing names a catalog model and one of its photos,
// carries plain vector path data and no picture, lies in that photo's frame (the curated positions sit on its
// silhouette), and paints in every drawing style and colour preset.
[SupportedOSPlatform("windows")]
public sealed class DeviceLineArtTests
{
    private static string DrawingFolder => DeviceLineArtDb.FolderOf(DeviceReferenceTests.ShippedCatalog);

    private static string CockpitDrawingFolder =>
        DeviceLineArtDb.CockpitFolderOf(Path.Combine(Path.GetDirectoryName(DeviceReferenceTests.ShippedCatalog.TrimEnd('\\', '/'))!, "Cockpits"));

    public static string OutputFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_REFERENCE_PREVIEW");
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "drawings") : configured;
        }
    }

    [Fact]
    public void EveryShippedDrawing_NamesAModelAndItsPhoto_AndIsPlainVectorData()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        List<string> problems = [];
        List<DeviceLineArt> arts = DeviceLineArtDb.LoadFolder(DrawingFolder, problems);
        List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog), null);
        foreach (DeviceLineArt art in arts)
        {
            string name = Path.GetFileName(art.File);
            RefDevice? model = catalog.Find(art.Model);
            if (model == null)
            {
                problems.Add(name + ": unknown model " + art.Model);
                continue;
            }
            // The photo it lies on: a curated set's photo or a photo reference of the model, by its content hash.
            IEnumerable<string> hashes = sets.Where(s => s.Model == model.Id).Select(s => s.PhotoSha256).Concat(model.Photos.Select(p => p.Sha256));
            if (DeviceLineArtDb.PhotoHashOf(art, hashes).Length == 0)
                problems.Add(name + ": its key names no photo hash of " + model.Id);
            if (!name.StartsWith(model.Id + ".", StringComparison.Ordinal))
                problems.Add(name + ": the file is not named after its model");
            problems.AddRange(PlainVectorProblems(art).Select(p => name + ": " + p));
        }
        foreach (DeviceLineArt art in DeviceLineArtDb.LoadFolder(CockpitDrawingFolder, problems))
        {
            if (art.Aircraft.Length == 0 || art.Device.Length == 0)
                problems.Add(Path.GetFileName(art.File) + ": a cockpit drawing names its aircraft and device");
            problems.AddRange(PlainVectorProblems(art).Select(p => Path.GetFileName(art.File) + ": " + p));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    private static IEnumerable<string> PlainVectorProblems(DeviceLineArt art)
    {
        string raw = File.ReadAllText(art.File);
        if (raw.Contains("http", StringComparison.OrdinalIgnoreCase) || raw.Contains("base64", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("data:", StringComparison.OrdinalIgnoreCase) || raw.Contains("<image", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("\"sha256\"", StringComparison.OrdinalIgnoreCase))
            yield return "carries an address, a hash field or embedded picture data";
        if (!art.Paths.Any(p => p.IsOutline))
            yield return "no silhouette";
        if (art.Key.Length != 16)
            yield return "no key";
        foreach (string d in art.Paths.Select(p => p.D).Concat(art.Fills.Select(f => f.D)))
        {
            string? problem = null;
            try
            {
                List<LineArtCommand> commands = DeviceLineArt.ParsePath(d);
                double side = Math.Max(art.Width, art.Height);
                if (commands.SelectMany(c => c.P).Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < -side * 0.1 || v > side * 1.1))
                    problem = "path data outside the picture";
            }
            catch (FormatException ex)
            {
                problem = "path data: " + ex.Message;
            }
            if (problem != null)
            {
                yield return problem;
                yield break;
            }
        }
    }

    // The curated positions of the drawing's photo fall on its silhouette (or next to it: parts on the edge).
    [Fact]
    public void CuratedPositions_LieOnTheDrawingsSilhouette()
    {
        List<DeviceLineArt> arts = DeviceLineArtDb.LoadFolder(DrawingFolder, null);
        List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog), null);
        List<string> problems = [];
        int checkedSets = 0;
        foreach (DeviceLineArt art in arts)
        {
            DeviceLayoutSet? set = sets.FirstOrDefault(s => s.Model == art.Model && DeviceLineArtDb.Matches(art, s.PhotoSha256));
            if (set == null)
                continue;
            checkedSets++;
            foreach (KeyValuePair<string, double[]> pair in set.Markers.Where(p => !art.Covers(p.Value[0], p.Value[1], 0.035)))
                problems.Add(Path.GetFileName(art.File) + ": " + pair.Key + " at " + pair.Value[0].ToString("0.000") + "," + pair.Value[1].ToString("0.000") + " is off the silhouette");
            if (set.Callouts != null && Math.Abs(set.Callouts.PictureWidth / set.Callouts.PictureHeight - art.Width / art.Height) > 0.01)
                problems.Add(Path.GetFileName(art.File) + ": another aspect than the photo's sheet");
        }
        Assert.True(problems.Count == 0, checkedSets + " drawings with curated positions; " + string.Join(Environment.NewLine, problems.Take(40)));
    }

    [Fact]
    public void DrawingStyle_ParsesPresetsAndColours()
    {
        DrawingStyle style = DrawingStyle.Parse("mode=lines;preset=manual;accent=#abc");
        Assert.False(style.IsCel);
        Assert.Equal("manual", style.Preset);
        Assert.Equal("#AABBCC", style.Accent);
        DrawingColors manual = style.Resolve(dark: false);
        Assert.Equal("#F2F2F2", manual.Ink);
        Assert.Equal("#0B0F14", manual.Paper);
        Assert.Equal("#AABBCC", manual.Accent);
        Assert.Equal(style.ToString(), DrawingStyle.Parse(style.ToString()).ToString());
        // JoyPro's colours follow the theme; the default style is cel-shaded.
        Assert.True(DrawingStyle.Default.IsCel);
        Assert.NotEqual(DrawingStyle.Default.Resolve(true).Paper, DrawingStyle.Default.Resolve(false).Paper);
        Assert.Equal("#111111", DrawingStyle.Parse("preset=print").Resolve(true).Ink);
        Assert.Equal("#1D4E89", DrawingStyle.Parse("preset=blueprint").Resolve(false).Paper);
        List<string> args = ["--drawing-preset", "custom", "--drawing-ink", "#ff0000", "--other", "x"];
        DrawingStyle? fromOptions = DrawingStyle.FromOptions(args, DrawingStyle.Default);
        Assert.NotNull(fromOptions);
        Assert.Equal("#FF0000", fromOptions!.Resolve(false).Ink);
        Assert.Equal(["--other", "x"], args);
    }

    // A sample drawing in every style and preset: the page has the preset's paper, the strokes its ink, the cel style
    // its fills; written to the preview folder for a look.
    [Fact]
    public void Renders_InEveryStyleAndPreset()
    {
        DeviceLineArt art = SampleDrawing();
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            foreach (string preset in new[] { "joypro", "manual", "blueprint", "print" })
            {
                foreach (string mode in new[] { "lines", "cel" })
                {
                    foreach (bool dark in new[] { false, true })
                    {
                        DrawingStyle style = DrawingStyle.Parse("mode=" + mode + ";preset=" + preset);
                        DrawingColors colors = style.Resolve(dark);
                        string file = Path.Combine(outDir, "drawing-" + preset + "-" + mode + (dark ? "-dark" : "-light") + ".png");
                        Render(new LineArtImage(art, style) { Dark = dark }, 400, 300, file);
                        using SKBitmap bitmap = SKBitmap.Decode(file);
                        SKColor corner = bitmap.GetPixel(4, 4);
                        Assert.Equal(colors.Paper, Hex(corner));
                        // The outline crosses the middle row; the cel style fills the body.
                        SKColor body = bitmap.GetPixel(200, 150);
                        if (mode == "cel")
                            Assert.Equal("#444444", Hex(body));
                        else
                            Assert.Equal(colors.Paper, Hex(body));
                        // The silhouette crosses the middle row: the ink (in the cel style near-black, on a dark page the ink).
                        bool inked = Enumerable.Range(0, 400).Any(x => Near(bitmap.GetPixel(x, 150), colors.StrokeFor(true, outline: true)) || Near(bitmap.GetPixel(x, 150), colors.Ink));
                        Assert.True(inked, file + ": no stroke in the drawing's colour");
                    }
                }
            }
        });
    }

    // The engine's sheets paint the same drawing with the same colours (EngineLineArtBitmap through the CLI is covered
    // by the engine smoke test; here the SVG of the shared code).
    [Fact]
    public void Svg_UsesTheStylesColours()
    {
        DeviceLineArt art = SampleDrawing();
        string lines = art.ToSvg(DrawingStyle.Parse("mode=lines;preset=manual").Resolve(false));
        Assert.Contains("fill=\"#0B0F14\"", lines);
        Assert.Contains("stroke=\"#F2F2F2\"", lines);
        Assert.DoesNotContain("#444444", lines);
        string cel = art.ToSvg(DrawingStyle.Parse("mode=cel;preset=print").Resolve(false));
        Assert.Contains("fill=\"#444444\"", cel);
        Assert.Contains("stroke=\"#141414\"", cel);
    }

    // Three stroke weights: the silhouette, the controls (detail) and fine lines (minor, 0.65 of the detail width).
    [Fact]
    public void StrokeWeights_OutlineDetailMinor()
    {
        DeviceLineArt art = DeviceLineArt.Parse("""
            { "format": "joypro-device-drawing", "version": 1, "model": "sample", "key": "0123456789abcdef", "width": 100, "height": 100,
              "outlineWidth": 3, "detailWidth": 2,
              "paths": [ { "role": "outline", "d": "M10,10L90,10L90,90Z" }, { "role": "detail", "d": "M20,20L30,30" }, { "role": "minor", "d": "M40,40L50,50" } ] }
            """, "weights.json");
        Assert.Equal(3, art.StrokeWidth(art.Paths[0], cel: false), 3);
        Assert.Equal(2, art.StrokeWidth(art.Paths[1], cel: false), 3);
        Assert.Equal(1.3, art.StrokeWidth(art.Paths[2], cel: false), 3);
        Assert.True(art.Paths[2].IsMinor && !art.Paths[2].IsOutline);
    }

    private static DeviceLineArt SampleDrawing() => DeviceLineArt.Parse("""
        { "format": "joypro-device-drawing", "version": 1, "model": "sample", "view": "main", "key": "0123456789abcdef",
          "width": 400, "height": 300, "outlineWidth": 3, "detailWidth": 2,
          "paths": [ { "role": "outline", "d": "M100,50L300,50Q340,50 340,90L340,250L60,250L60,90Q60,50 100,50Z" },
                     { "role": "detail", "d": "M120,100C150,80 180,80 200,100" } ],
          "fills": [ { "color": "#444444", "rule": "evenodd", "d": "M100,50L300,50Q340,50 340,90L340,250L60,250L60,90Q60,50 100,50Z" } ] }
        """, "sample.json");

    private static void Render(IImage image, int width, int height, string file)
    {
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
            context.DrawImage(image, new Rect(image.Size), new Rect(0, 0, width, height));
        bitmap.Save(file);
    }

    private static string Hex(SKColor c) => "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2");

    private static bool Near(SKColor c, string hex)
    {
        int[] want = DrawingStyle.Rgb(hex);
        return Math.Abs(c.Red - want[0]) < 40 && Math.Abs(c.Green - want[1]) < 40 && Math.Abs(c.Blue - want[2]) < 40;
    }
}
