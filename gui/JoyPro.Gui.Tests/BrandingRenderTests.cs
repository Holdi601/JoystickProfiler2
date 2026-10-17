using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The JoyPro mark and banner (Styles/Branding.axaml), the game badges, and the bottom bar of the main window, rendered
// offscreen. Needs the whole application, so it renders only when JOYPRO_BRANDING_OUT names an output folder (the
// repository's docs/branding for the previews) and must then run alone:
//   set JOYPRO_BRANDING_OUT=<repo>\docs\branding & dotnet test gui/JoyPro.Gui.Tests --filter BrandingRenderTests
// With JOYPRO_BRANDING_REPO=<repo> it also writes the icons (16 to 256 px PNG frames) rendered from the vector mark:
// branding/base_joypro.ico (mark), gui/JoyPro.Gui/Assets/joypro2_gui.ico (mark + "2": GUI and tray) and
// engine/assets/engine_console.ico (mark + ">_"). branding/generate-icons.ps1 runs it.
[SupportedOSPlatform("windows")]
public sealed class BrandingRenderTests
{
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void RenderBrandingAndCheckTheBottomBar()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string? repo = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_REPO");

        AvaloniaRenderThread.Run(() =>
        {
            IImage Resource(string key) => (IImage)Application.Current!.FindResource(key)!;

            // The mark, the GUI / tray icon (mark + "2") and the engine icon (mark + ">_") at icon sizes.
            foreach ((string name, string key, string ico) in new[]
                     {
                         ("joypro-mark", "Brand.Logo", Path.Combine("branding", "base_joypro.ico")),
                         ("icon-app", "Brand.AppIcon", Path.Combine("gui", "JoyPro.Gui", "Assets", "joypro2_gui.ico")),
                         ("icon-engine", "Brand.EngineIcon", Path.Combine("engine", "assets", "engine_console.ico"))
                     })
            {
                Dictionary<int, byte[]> pngs = [];
                foreach (int size in IconSizes)
                {
                    // 16 and 24 px: the simplified composition with the bigger numeral / prompt.
                    string frameKey = size <= 24 && Application.Current!.TryFindResource(key + ".Small", out _) ? key + ".Small" : key;
                    using RenderTargetBitmap bitmap = RenderControl(new Image { Source = Resource(frameKey), Width = size, Height = size }, size, size, null);
                    using MemoryStream stream = new();
                    bitmap.Save(stream);
                    pngs[size] = stream.ToArray();
                    if (size is 16 or 32 or 48 or 256)
                        File.WriteAllBytes(Path.Combine(outDir, name + "-" + size + ".png"), pngs[size]);
                }
                if (!string.IsNullOrWhiteSpace(repo))
                    WriteIco(Path.Combine(repo, ico), pngs);
            }

            // The mark with the "2" at 16 to 160 px on a light and a dark background.
            foreach ((string name, string color) in new[] { ("light", "#F1F5F9"), ("dark", "#111827") })
            {
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, Margin = new Thickness(16) };
                foreach (int size in new[] { 16, 32, 48, 96, 160 })
                    row.Children.Add(new Image { Source = Resource("Brand.AppIcon"), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                row.Children.Add(new Image { Source = Resource("Brand.EngineIcon"), Width = 96, Height = 96, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                using RenderTargetBitmap sheet = RenderControl(row, 600, 200, Color.Parse(color));
                sheet.Save(Path.Combine(outDir, "mark-with-2-on-" + name + ".png"));
            }

            // The variants side by side (Brand.*.A/B/C and Brand.Wordmark.W1/W2/W3).
            StackPanel variants = new() { Spacing = 14, Margin = new Thickness(16) };
            foreach (string v in new[] { "A", "B", "C", "Small" })
            {
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 22 };
                row.Children.Add(new TextBlock { Text = "Icon " + v, Width = 60, Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                foreach (int size in new[] { 16, 32, 48, 128 })
                    row.Children.Add(new Image { Source = Resource("Brand.AppIcon." + v), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                foreach (int size in new[] { 32, 128 })
                    row.Children.Add(new Image { Source = Resource("Brand.EngineIcon." + v), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                variants.Children.Add(row);
            }
            foreach (string v in new[] { "W1", "W2", "W3" })
            {
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 22 };
                row.Children.Add(new TextBlock { Text = "Title " + v, Width = 60, Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                row.Children.Add(new Image { Source = Resource("Brand.Logo"), Width = 56, Height = 56 });
                row.Children.Add(new Image { Source = Resource("Brand.Wordmark." + v), Height = 44, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                row.Children.Add(new Image { Source = Resource("Brand.Wordmark." + v), Height = 18, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                variants.Children.Add(row);
            }
            using (RenderTargetBitmap sheet = RenderControl(new Border { Background = (IBrush)Application.Current!.FindResource("Brand.BannerBrush")!, Child = variants }, 760, 900, Color.Parse("#0B1220")))
                sheet.Save(Path.Combine(outDir, "variants.png"));

            // The game badges (games without a Steam cover on this PC).
            WrapPanel badges = new() { Margin = new Thickness(12), Width = 900 };
            foreach (string game in new[] { "DCS", "IL2Game", "IL2Korea", "MSFS", "XPlane", "FalconBMS", "WarThunder", "VTOLVR", "NuclearOption", "Wardogs",
                         "Arma2", "Arma2OA", "Arma3", "ArmaReforger", "HLLVietnam", "X4", "EliteDangerous", "StarCitizen",
                         "IL21946", "IL2CoD", "IL2CoDBlitz", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6", "SomeNewGame" })
            {
                badges.Children.Add(new Border
                {
                    Width = 64, Height = 96, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(6), ClipToBounds = true,
                    Child = new Views.Controls.GameBadge { GameId = game, Title = game == "SomeNewGame" ? "Some New Game" : string.Empty }
                });
            }
            using (RenderTargetBitmap sheet = RenderControl(badges, 924, 350,Color.Parse("#1F2937")))
                sheet.Save(Path.Combine(outDir, "game-badges.png"));

            // The main window: banner wide and narrow, bottom bar with the output panel open and closed.
            MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                StatusText = "Done: exported DCS World (status line must stay readable)"
            };
            vm.CurrentStage = UiStage.Start;
            foreach ((int width, int height) in new[] { (1920, 1080), (1500, 950), (1200, 760) })
            {
                foreach (bool expanded in new[] { false, true })
                {
                    vm.EngineOutputExpanded = expanded;
                    MainWindow main = new() { DataContext = vm, Width = width, Height = height };
                    Control page = Layout(main, width, height);
                    AssertBottomBarClear(page, vm.StatusText, width + "x" + height + (expanded ? " output open" : " output closed"));
                    if (width == 1500 && !expanded)
                        SaveCut(page, width, height, new Rect(0, 0, width, 260), Path.Combine(outDir, "banner-wide.png"));
                    if (width == 1200 && !expanded)
                        SaveCut(page, width, height, new Rect(0, 0, width, 240), Path.Combine(outDir, "banner-narrow.png"));
                    SaveCut(page, width, height, new Rect(0, Math.Max(0, height - (expanded ? 420 : 160)), width, expanded ? 420 : 160),
                        Path.Combine(outDir, "bottom-bar-" + width + "-" + (expanded ? "output-open" : "output-closed") + ".png"));
                }
            }
        });
    }

    // Round 2 of the "2" / ">_" concepts and their titles (Styles/BrandingConcepts.axaml, made by
    // branding/generate-branding.py from branding/branding_concepts.py): docs/branding/variants-round2.png.
    // Each concept at 256, 48, 32 and 16 px (16: its small form) on dark and light, the engine icon, and the title.
    [Fact]
    public void RenderRound2Concepts()
    {
        RenderSheet("variants-round2.png", "JoyPro \"2\" - round 2", 3200,
        [
                         ("LineArt", "1  LineArt - double-line outline like the original logo", "Brand.AppIcon.LineArt", "Brand.EngineIcon.LineArt", "Brand.Wordmark.LineArt"),
                         ("Formation", "2  Formation - built from wing, sweep line and trailing edge", "Brand.AppIcon.Formation", "Brand.EngineIcon.Formation", "Brand.Wordmark.Formation"),
                         ("Stencil", "3  Stencil - cut out of a second kite shield", "Brand.AppIcon.Stencil", "Brand.EngineIcon.Stencil", "Brand.Wordmark.Stencil"),
                         ("Hud", "4  Hud - segmented readout with chevron underline", "Brand.AppIcon.Hud", "Brand.EngineIcon.Hud", "Brand.Wordmark.Hud"),
                         ("Metal", "5  Metal - faceted bevels, blue edge", "Brand.AppIcon.Metal", "Brand.EngineIcon.Metal", "Brand.Wordmark.Metal"),
                         ("Integrated", "6  Integrated - the shield's edge and tip form the 2", "Brand.AppIcon.Integrated", "Brand.EngineIcon.Integrated", "Brand.Wordmark.Integrated"),
                         ("A", "Round 1  A / W2", "Brand.AppIcon.A", "Brand.EngineIcon.A", "Brand.Wordmark.W2"),
        ]);
    }

    // Round 3: Icon A's "2" built from separate faceted pieces (FacetA/B/C) with the W3 title: docs/branding/variants-round3.png.
    [Fact]
    public void RenderRound3Faceted()
    {
        RenderSheet("variants-round3.png", "JoyPro \"2\" - round 3: faceted pieces (Icon A placement, title W3)", 2000,
        [
            ("FacetA", "FacetA - pieces with thin gaps, white / light grey", "Brand.AppIcon.FacetA", "Brand.EngineIcon.FacetA", "Brand.Wordmark.W3FacetA"),
            ("FacetB", "FacetB - joined by dark seams, each piece in a light and a grey plane", "Brand.AppIcon.FacetB", "Brand.EngineIcon.FacetB", "Brand.Wordmark.W3FacetB"),
            ("FacetC", "FacetC - pieces with gaps, each in two planes", "Brand.AppIcon.FacetC", "Brand.EngineIcon.FacetC", "Brand.Wordmark.W3FacetC"),
            ("A", "Before: A / W3 (one solid shape)", "Brand.AppIcon.A", "Brand.EngineIcon.A", "Brand.Wordmark.W3"),
        ]);
    }

    // Round 4: the faceted "2" built only from the mark's angles (AngleA/B/C): docs/branding/variants-round4.png.
    [Fact]
    public void RenderRound4Angled()
    {
        RenderSheet("variants-round4.png", "JoyPro \"2\" - round 4: the mark's angles (24, 32, 48.5, 51, 58, 80 deg)", 2000,
        [
            ("AngleA", "AngleA - mild: top 8 deg, foot 3 deg, right side 84 deg, diagonal at the wing sweep", "Brand.AppIcon.AngleA", "Brand.EngineIcon.AngleA", "Brand.Wordmark.W3AngleA"),
            ("AngleB", "AngleB (default) - medium: top 14 deg, foot 5 deg, right side at the fuselage angle", "Brand.AppIcon.AngleB", "Brand.EngineIcon.AngleB", "Brand.Wordmark.W3AngleB"),
            ("AngleC", "AngleC - strong: top at the shield edge (24 deg), diagonal at the shield's lower edge", "Brand.AppIcon.AngleC", "Brand.EngineIcon.AngleC", "Brand.Wordmark.W3AngleC"),
            ("FacetA", "Before: FacetA / W3FacetA (round 3)", "Brand.AppIcon.FacetA", "Brand.EngineIcon.FacetA", "Brand.Wordmark.W3FacetA"),
        ]);
    }

    // Round 5: the "2" from strokes parallel to the shield's lower right edge (the user's sketches): variants-round5.png.
    [Fact]
    public void RenderRound5Strokes()
    {
        RenderSheet("variants-round5.png", "JoyPro \"2\" - round 5: strokes parallel to the shield's edge", 2900,
        [
            ("Rim3", "Rim3 - three strokes (sketch 1), round ends, blue foot", "Brand.AppIcon.Rim3", "Brand.EngineIcon.Rim3", "Brand.Wordmark.W3Rim3"),
            ("Rim3Tip", "Rim3Tip - three strokes, chamfered ends, blue tip on the diagonal", "Brand.AppIcon.Rim3Tip", "Brand.EngineIcon.Rim3Tip", "Brand.Wordmark.W3Rim3Tip"),
            ("Hatch", "Hatch - 2 ticks, long stroke, 3 ticks (sketch 2), blue outer foot tick", "Brand.AppIcon.Hatch", "Brand.EngineIcon.Hatch", "Brand.Wordmark.W3Hatch"),
            ("HatchRim", "HatchRim - Hatch with a light second rim along the edge", "Brand.AppIcon.HatchRim", "Brand.EngineIcon.HatchRim", "Brand.Wordmark.W3HatchRim"),
            ("HatchChamfer", "HatchChamfer - heavier chamfered strokes, blue end of the diagonal", "Brand.AppIcon.HatchChamfer", "Brand.EngineIcon.HatchChamfer", "Brand.Wordmark.W3HatchChamfer"),
            ("AngleB", "Current default: AngleB / W3AngleB (round 4)", "Brand.AppIcon.AngleB", "Brand.EngineIcon.AngleB", "Brand.Wordmark.W3AngleB"),
        ]);
    }

    // Round 6: variations of Rim3Tip (thickness, gap, offsets, cut angle, where the blue sits, rim line): variants-round6.png.
    [Fact]
    public void RenderRound6Rim3Tip()
    {
        RenderSheet("variants-round6.png", "JoyPro \"2\" - round 6: variations of Rim3Tip", 3300,
        [
            ("Rim3Tip", "Rim3Tip (round 5) for comparison", "Brand.AppIcon.Rim3Tip", "Brand.EngineIcon.Rim3Tip", "Brand.Wordmark.W3Rim3Tip"),
            ("TipThin", "TipThin - thinner strokes, tighter gap, a little more offset", "Brand.AppIcon.TipThin", "Brand.EngineIcon.TipThin", "Brand.Wordmark.W3TipThin"),
            ("TipBold", "TipBold - heavier strokes, looser gap, longer top and foot", "Brand.AppIcon.TipBold", "Brand.EngineIcon.TipBold", "Brand.Wordmark.W3TipBold"),
            ("TipEdgeCut", "TipEdgeCut - ends cut parallel to the shield's upper edges, blue as a separate segment", "Brand.AppIcon.TipEdgeCut", "Brand.EngineIcon.TipEdgeCut", "Brand.Wordmark.W3TipEdgeCut"),
            ("TipBlueTop", "TipBlueTop - the blue on the top stroke", "Brand.AppIcon.TipBlueTop", "Brand.EngineIcon.TipBlueTop", "Brand.Wordmark.W3TipBlueTop"),
            ("TipRimLine", "TipRimLine - with a thin light line along the rim", "Brand.AppIcon.TipRimLine", "Brand.EngineIcon.TipRimLine", "Brand.Wordmark.W3TipRimLine"),
            ("TipClear2", "TipClear2 - longer top and foot, more offset: reads more clearly as a 2", "Brand.AppIcon.TipClear2", "Brand.EngineIcon.TipClear2", "Brand.Wordmark.W3TipClear2"),
        ]);
    }

    // Round 7: TipBold with a clear constant gap to the shield and aligned bar ends (the default): variants-round7.png.
    [Fact]
    public void RenderRound7Aligned()
    {
        RenderSheet("variants-round7.png", "JoyPro \"2\" - round 7: TipBold aligned (default: TipBoldAligned)", 1450,
        [
            ("TipBold", "TipBold (round 6)", "Brand.AppIcon.TipBold", "Brand.EngineIcon.TipBold", "Brand.Wordmark.W3TipBold"),
            ("TipBoldAligned", "TipBoldAligned (default) - clear gap to the shield, bar ends on common cut lines", "Brand.AppIcon.TipBoldAligned", "Brand.EngineIcon.TipBoldAligned", "Brand.Wordmark.W3TipBoldAligned"),
            ("TipBoldAlignedWide", "TipBoldAlignedWide - the same with a wider gap", "Brand.AppIcon.TipBoldAlignedWide", "Brand.EngineIcon.TipBoldAlignedWide", "Brand.Wordmark.W3TipBoldAlignedWide"),
        ]);
    }

    // Round 8: the engine mark - a prompt from strokes parallel to the shield edge, four places for the blue:
    // variants-round8.png. Each row: the engine icon at 256/48/32/16 px, then the GUI icon (TipBoldAligned) for context.
    [Fact]
    public void RenderRound8EngineMarks()
    {
        RenderSheet("variants-round8.png", "JoyPro engine mark - round 8: a prompt in strokes (right of each row: the GUI icon)", 1900,
        [
            ("PromptTopBlue", "PromptTopBlue - blue: the second top tick", "Brand.EngineIcon.PromptTopBlue", "Brand.AppIcon.TipBoldAligned", "Brand.Wordmark.W3TipBoldAligned"),
            ("PromptArmBlue", "PromptArmBlue - blue: the long stroke of the \">\"", "Brand.EngineIcon.PromptArmBlue", "Brand.AppIcon.TipBoldAligned", "Brand.Wordmark.W3TipBoldAligned"),
            ("PromptCursorBlue", "PromptCursorBlue - blue: the last tick of the \"_\" (blinking cursor)", "Brand.EngineIcon.PromptCursorBlue", "Brand.AppIcon.TipBoldAligned", "Brand.Wordmark.W3TipBoldAligned"),
            ("PromptRowBlue", "PromptRowBlue - blue: the whole \"_\" row", "Brand.EngineIcon.PromptRowBlue", "Brand.AppIcon.TipBoldAligned", "Brand.Wordmark.W3TipBoldAligned"),
        ]);
    }

    // Round 9: the engine mark traced from the user's sketch (images/6.png): docs/branding/variants-round9.png. Each row:
    // the 256 px render with the sketch's strokes laid over it (magenta, half transparent, mapped with the same
    // transform as the trace), the clean render at 256/48/32/16 px on dark and light, and the GUI icon for context.
    [Fact]
    public void RenderRound9Traced()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string overlayFile = FindRepoFile(Path.Combine("docs", "branding", "sketch-6-strokes.png"));

        AvaloniaRenderThread.Run(() =>
        {
            ResourceDictionary concepts = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://JoyPro.Gui/Styles/BrandingConcepts.axaml"));
            IImage Find(string key) => (IImage)(concepts.TryGetResource(key, null, out object? value) ? value! : Application.Current!.FindResource(key)!);
            using Bitmap overlay = new(overlayFile);

            StackPanel sheet = new() { Spacing = 16, Margin = new Thickness(20) };
            sheet.Children.Add(new TextBlock { Text = "JoyPro engine mark - round 9: traced from the sketch (magenta: the sketch's strokes)", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
            foreach ((string name, string label) in new[]
                     {
                         ("TraceTopBlue", "TraceTopBlue - blue: his blue tick at the top (as drawn)"),
                         ("TraceHookBlue", "TraceHookBlue - blue: the hooked stroke"),
                         ("TraceRowTickBlue", "TraceRowTickBlue - blue: the middle tick of the bottom row"),
                         ("TraceLastBlue", "TraceLastBlue - blue: the last tick of the bottom row"),
                         ("TraceSketch", "TraceSketch - his colours (#808285, #58595B, #00AACC)"),
                     })
            {
                string key = "Brand.EngineIcon." + name;
                StackPanel block = new() { Spacing = 6 };
                block.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
                Canvas check = new() { Width = 256, Height = 256, ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#111827")) };
                check.Children.Add(new Image { Source = Find(key), Width = 256, Height = 256 });
                Image sketch = new() { Source = overlay, Width = 865 * 2.56 / 4.95, Height = 679 * 2.56 / 4.92, Stretch = Stretch.Fill, Opacity = 0.5 };
                Canvas.SetLeft(sketch, -248.5 * 2.56 / 4.95 + 50 * 2.56);
                Canvas.SetTop(sketch, (5.75 - 21 / 4.92) * 2.56);
                check.Children.Add(sketch);
                row.Children.Add(check);
                foreach (string background in new[] { "#111827", "#F1F5F9" })
                {
                    StackPanel icons = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(10) };
                    foreach (int size in new[] { 256, 48, 32, 16 })
                        icons.Children.Add(new Image { Source = Find(size <= 24 ? key + ".Small" : key), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    row.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(background)), CornerRadius = new CornerRadius(10), Child = icons });
                }
                row.Children.Add(new Image { Source = Find("Brand.AppIcon"), Width = 64, Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                block.Children.Add(row);
                sheet.Children.Add(block);
            }
            using RenderTargetBitmap bitmap = RenderControl(sheet, 1700, 1720, Color.Parse("#0B1220"));
            bitmap.Save(Path.Combine(outDir, "variants-round9.png"));
        });
    }

    // Round 10: the ">_" the user drew on our icon (images/9.png), cleaned up: docs/branding/variants-round10.png. Each row:
    // the 256 px render with his strokes laid over it (magenta; his canvas 186..915 x 69..798 = the 100 grid), the
    // clean render at 256/48/32/16 px on dark and light, and the GUI icon for context. The overlay matches the strokes
    // where the icon is not shrunk (Trace10Exact is shrunk least).
    [Fact]
    public void RenderRound10Prompt()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string overlayFile = FindRepoFile(Path.Combine("docs", "branding", "sketch-9-strokes.png"));

        AvaloniaRenderThread.Run(() =>
        {
            ResourceDictionary concepts = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://JoyPro.Gui/Styles/BrandingConcepts.axaml"));
            IImage Find(string key) => (IImage)(concepts.TryGetResource(key, null, out object? value) ? value! : Application.Current!.FindResource(key)!);
            using Bitmap overlay = new(overlayFile);
            double k = 2.56 / 7.29;

            StackPanel sheet = new() { Spacing = 16, Margin = new Thickness(20) };
            sheet.Children.Add(new TextBlock { Text = "JoyPro engine mark - round 10: the drawn >_ cleaned up (magenta: the drawing)", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
            foreach ((string name, string label) in new[]
                     {
                         ("Trace10Exact", "Trace10Exact - his layout, width 3.6, blue on the long bar and the last tick (as drawn)"),
                         ("Trace10Large", "Trace10Large - width 4.6, the group scaled up"),
                         ("Trace10Ticks3", "Trace10Ticks3 - Large with three short ticks in the >"),
                         ("Trace10BarBlue", "Trace10BarBlue - Large, blue only on the long bar"),
                     })
            {
                string key = "Brand.EngineIcon." + name;
                StackPanel block = new() { Spacing = 6 };
                block.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
                Canvas check = new() { Width = 256, Height = 256, ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#111827")) };
                check.Children.Add(new Image { Source = Find(key), Width = 256, Height = 256 });
                Image sketch = new() { Source = overlay, Width = 1122 * k, Height = 893 * k, Stretch = Stretch.Fill, Opacity = 0.45 };
                Canvas.SetLeft(sketch, -186 * k);
                Canvas.SetTop(sketch, -69 * k);
                check.Children.Add(sketch);
                row.Children.Add(check);
                foreach (string background in new[] { "#111827", "#F1F5F9" })
                {
                    StackPanel icons = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(10) };
                    foreach (int size in new[] { 256, 48, 32, 16 })
                        icons.Children.Add(new Image { Source = Find(size <= 24 ? key + ".Small" : key), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    row.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(background)), CornerRadius = new CornerRadius(10), Child = icons });
                }
                row.Children.Add(new Image { Source = Find("Brand.AppIcon"), Width = 64, Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                block.Children.Add(row);
                sheet.Children.Add(block);
            }
            using RenderTargetBitmap bitmap = RenderControl(sheet, 1700, 1400, Color.Parse("#0B1220"));
            bitmap.Save(Path.Combine(outDir, "variants-round10.png"));
        });
    }

    // Round 11: round 10 with the user's corrections (two short ticks, solid bars of one grey with the same contrast on
    // dark and light, long bars with common top and bottom, the "_" cut level into one band):
    // docs/branding/variants-round11.png. Each variant: the overlay row as in round 10, then a 512 px close-up of the
    // strokes on dark and on light.
    [Fact]
    public void RenderRound11Prompt()
    {
        RenderTraceSheet("variants-round11.png",
            "JoyPro engine mark - round 11: solid grey bars #707C8E (same contrast on dark and light), long bars with one top and one bottom line, the _ cut level (magenta: the drawing)",
            new[]
            {
                ("Trace11Exact", "Trace11Exact - width 3.6, blue on the long bar and the last _ tick"),
                ("Trace11Large", "Trace11Large - width 4.6, the group scaled up, blue on the long bar and the last _ tick"),
                ("Trace11BarBlue", "Trace11BarBlue - Large, blue only on the long bar"),
            }, 2680);
    }

    // Round 12: Trace11Large with short ticks half as long as the long bars and the "_" band lowered onto the ">"'s
    // bottom line (or 2 units above it), 3 / 4 / 5 ticks: docs/branding/variants-round12.png, laid out as round 11.
    [Fact]
    public void RenderRound12Prompt()
    {
        RenderTraceSheet("variants-round12.png",
            "JoyPro engine mark - round 12: Trace11Large, short ticks half the long bars, the _ band lowered (magenta: the drawing)",
            new[]
            {
                ("Trace12T3", "Trace12T3 - band on the bottom line of the >, 3 ticks"),
                ("Trace12T4", "Trace12T4 - band on the bottom line of the >, 4 ticks"),
                ("Trace12T5", "Trace12T5 - band on the bottom line of the >, 5 ticks"),
                ("Trace12T3Up", "Trace12T3Up - band 2 units above the bottom line, 3 ticks"),
                ("Trace12T4Up", "Trace12T4Up - band 2 units above the bottom line, 4 ticks"),
                ("Trace12T5Up", "Trace12T5Up - band 2 units above the bottom line, 5 ticks"),
            }, 5120);
    }

    // The sheet of rounds 11 and 12: per variant the 256 px render with the drawing of images/9.png laid over it, the
    // icon at 256/48/32/16 px on dark and light, the GUI icon, and a 512 px close-up of the strokes (the icon from 48
    // to 100.8 units across and down, at 9.7 px per unit) on dark and light.
    private static void RenderTraceSheet(string file, string heading, (string Name, string Label)[] rows, int height)
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string overlayFile = FindRepoFile(Path.Combine("docs", "branding", "sketch-9-strokes.png"));

        AvaloniaRenderThread.Run(() =>
        {
            ResourceDictionary concepts = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://JoyPro.Gui/Styles/BrandingConcepts.axaml"));
            IImage Find(string key) => (IImage)(concepts.TryGetResource(key, null, out object? value) ? value! : Application.Current!.FindResource(key)!);
            using Bitmap overlay = new(overlayFile);
            double k = 2.56 / 7.29;

            StackPanel sheet = new() { Spacing = 16, Margin = new Thickness(20) };
            sheet.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
            foreach ((string name, string label) in rows)
            {
                string key = "Brand.EngineIcon." + name;
                StackPanel block = new() { Spacing = 6 };
                block.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
                Canvas check = new() { Width = 256, Height = 256, ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#111827")) };
                check.Children.Add(new Image { Source = Find(key), Width = 256, Height = 256 });
                Image sketch = new() { Source = overlay, Width = 1122 * k, Height = 893 * k, Stretch = Stretch.Fill, Opacity = 0.45 };
                Canvas.SetLeft(sketch, -186 * k);
                Canvas.SetTop(sketch, -69 * k);
                check.Children.Add(sketch);
                row.Children.Add(check);
                foreach (string background in new[] { "#111827", "#F1F5F9" })
                {
                    StackPanel icons = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(10) };
                    foreach (int size in new[] { 256, 48, 32, 16 })
                        icons.Children.Add(new Image { Source = Find(size <= 24 ? key + ".Small" : key), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    row.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(background)), CornerRadius = new CornerRadius(10), Child = icons });
                }
                row.Children.Add(new Image { Source = Find("Brand.AppIcon"), Width = 64, Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                block.Children.Add(row);
                StackPanel closeUps = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
                foreach (string background in new[] { "#111827", "#F1F5F9" })
                {
                    Canvas closeUp = new() { Width = 512, Height = 512, ClipToBounds = true, Background = new SolidColorBrush(Color.Parse(background)) };
                    // 9.7 px per unit from 48 to 100.8: the strokes with a little room below them
                    Image big = new() { Source = Find(key), Width = 970, Height = 970 };
                    Canvas.SetLeft(big, -466);
                    Canvas.SetTop(big, -466);
                    closeUp.Children.Add(big);
                    closeUps.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = closeUp });
                }
                block.Children.Add(closeUps);
                sheet.Children.Add(block);
            }
            using RenderTargetBitmap bitmap = RenderControl(sheet, 1700, height, Color.Parse("#0B1220"));
            bitmap.Save(Path.Combine(outDir, file));
        });
    }

    private static string FindRepoFile(string relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // A comparison sheet of icon / title variants: each at 256, 48, 32 and 16 px (16: its small form) on dark and light,
    // the engine icon, and the banner title.
    private static void RenderSheet(string file, string heading, int height, (string Name, string Label, string App, string Engine, string Title)[] rows)
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BRANDING_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            ResourceDictionary concepts = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://JoyPro.Gui/Styles/BrandingConcepts.axaml"));
            IImage Find(string key) => (IImage)(concepts.TryGetResource(key, null, out object? value) ? value! : Application.Current!.FindResource(key)!);
            IBrush banner = (IBrush)Application.Current!.FindResource("Brand.BannerBrush")!;

            StackPanel sheet = new() { Spacing = 18, Margin = new Thickness(20) };
            sheet.Children.Add(new TextBlock { Text = heading, FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
            foreach ((string name, string label, string app, string engine, string title) in rows)
            {
                StackPanel block = new() { Spacing = 8 };
                block.Children.Add(new TextBlock { Text = label, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
                StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16 };
                foreach (string background in new[] { "#111827", "#F1F5F9" })
                {
                    StackPanel icons = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12) };
                    foreach (int size in new[] { 256, 48, 32, 16 })
                    {
                        string key = size <= 24 && name != "A" ? app + ".Small" : size <= 24 ? "Brand.AppIcon.Small" : app;
                        icons.Children.Add(new Image { Source = Find(key), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    }
                    icons.Children.Add(new Image { Source = Find(engine), Width = 64, Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    icons.Children.Add(new Image { Source = Find(name != "A" ? engine + ".Small" : "Brand.EngineIcon.Small"), Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    row.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(background)), CornerRadius = new CornerRadius(10), Child = icons });
                }
                block.Children.Add(row);
                StackPanel titleRow = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(14, 10) };
                titleRow.Children.Add(new Image { Source = Find(app), Width = 72, Height = 72 });
                titleRow.Children.Add(new Image { Source = Find(title), Height = 44, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                titleRow.Children.Add(new Image { Source = Find(title), Height = 18, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                block.Children.Add(new Border { Background = banner, CornerRadius = new CornerRadius(10), Child = titleRow, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
                sheet.Children.Add(block);
            }
            using RenderTargetBitmap bitmap = RenderControl(sheet, 1300, height, Color.Parse("#0B1220"));
            bitmap.Save(Path.Combine(outDir, file));
        });
    }

    private static Control Layout(MainWindow main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 3; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        return page;
    }

    // The status line of the bottom bar is never covered by the output panel or the page buttons.
    private static void AssertBottomBarClear(Control page, string statusText, string what)
    {
        // The bottom bar's status line (the Start page shows the same text in its busy box while JoyPro works).
        TextBlock status = page.GetVisualDescendantsOfType<TextBlock>().Where(t => t.Text == statusText && t.IsEffectivelyVisible)
            .OrderByDescending(t => BoundsIn(t, page).Y).First();
        Rect statusRect = BoundsIn(status, page);
        Assert.True(statusRect.Height > 0 && statusRect.Width > 40, what + ": the status line has no room (" + statusRect + ").");
        Border output = page.GetVisualDescendantsOfType<Border>().First(b => b.Name == "EngineOutputBorder");
        if (output.IsEffectivelyVisible)
            Assert.False(BoundsIn(output, page).Intersects(statusRect), what + ": the output panel covers the status line.");
        foreach (ToggleButton nav in page.GetVisualDescendantsOfType<ToggleButton>().Where(b => b.Classes.Contains("nav") && b.IsEffectivelyVisible))
        {
            Assert.False(BoundsIn(nav, page).Intersects(statusRect), what + ": the button " + nav.Content + " covers the status line.");
            Assert.True(BoundsIn(nav, page).Bottom <= page.Bounds.Height + 0.5, what + ": the button " + nav.Content + " is cut off at the bottom.");
        }
        Assert.True(statusRect.Bottom <= page.Bounds.Height + 0.5, what + ": the status line is below the window.");
    }

    private static Rect BoundsIn(Visual visual, Visual root)
    {
        Point? topLeft = visual.TranslatePoint(new Point(0, 0), root);
        Assert.NotNull(topLeft);
        return new Rect(topLeft!.Value, visual.Bounds.Size);
    }

    private static RenderTargetBitmap RenderControl(Control control, int width, int height, Color? background)
    {
        Border host = new() { Width = width, Height = height, Child = control, Background = background.HasValue ? new SolidColorBrush(background.Value) : Brushes.Transparent };
        Size size = new(width, height);
        host.Measure(size);
        host.Arrange(new Rect(size));
        AvaloniaRenderThread.Pump();
        host.Measure(size);
        host.Arrange(new Rect(size));
        RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(host);
        return bitmap;
    }

    private static void SaveCut(Control page, int width, int height, Rect cut, string file)
    {
        using RenderTargetBitmap whole = new(new PixelSize(width, height));
        whole.Render(page);
        PixelSize size = new((int)cut.Width, (int)cut.Height);
        using RenderTargetBitmap part = new(size);
        using (DrawingContext context = part.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#202020")), new Rect(0, 0, size.Width, size.Height));
            context.DrawImage(whole, cut, new Rect(0, 0, size.Width, size.Height));
        }
        part.Save(file);
    }

    // An .ico with one PNG entry per size (Windows picks the nearest size for the taskbar, title bar and tray).
    private static void WriteIco(string path, Dictionary<int, byte[]> pngs)
    {
        List<int> sizes = pngs.Keys.OrderBy(s => s).ToList();
        using FileStream file = File.Create(path);
        using BinaryWriter writer = new(file);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Count);
        int offset = 6 + 16 * sizes.Count;
        foreach (int size in sizes)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(pngs[size].Length);
            writer.Write(offset);
            offset += pngs[size].Length;
        }
        foreach (int size in sizes)
            writer.Write(pngs[size]);
    }
}
