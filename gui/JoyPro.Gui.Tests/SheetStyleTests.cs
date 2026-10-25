using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The cheat sheet style (docs/ui/layout-redesign.md "Sheet style"): the shared SheetStyle (presets, JSON, palette), the
// style panel (presets, own presets, modifier list), the global style surviving a restart, a device's own style in its
// sheet (saved at once, back after a switch, through "Share layout..." and its import), the engine options of "Create
// cheat sheets", the colours in what is drawn (the real engine's sheet and the GUI's preview, sampled), and the panel
// and the Layout page without overlaps at 1280x800 and 2267x1250, also with a big sheet font and a big UI text size.
// JOYPRO_SHEET_STYLE_OUT=<folder> keeps the renders (the panel and a sheet with three presets, light and dark).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class SheetStyleTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "sheet-style-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public SheetStyleTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ the shared style

    [Fact]
    public void Presets_AreComplete_AndTheDefaultsDrawTheBaseSheets()
    {
        foreach (string id in SheetStyle.PresetIds)
        {
            SheetStyle preset = SheetStyle.Preset(id);
            Assert.Equal(id, preset.Name);
            foreach (string color in new[] { preset.PageBackground, preset.TableBackground, preset.TableBorder, preset.HeaderBackground, preset.HeaderText,
                         preset.TableText, preset.LineColor, preset.MarkerButton, preset.MarkerAxis, preset.MarkerHat, preset.MarkerSlider, preset.TextColor })
                Assert.Matches("^#[0-9A-F]{8}$", color);
            Assert.NotEmpty(preset.Modifiers);
            Assert.NotEmpty(preset.Drawing);
        }
        // JoyPro's default is JoyPro 1's grey page, Print the white page: exactly the engine's base palettes.
        SheetPalette dark = SheetStyle.Preset("joypro").Resolve(true), darkBase = SheetPalette.Base(true);
        SheetPalette light = SheetStyle.Preset("print").Resolve(false), lightBase = SheetPalette.Base(false);
        Assert.Equal(JsonSerializer.Serialize(Fields(darkBase)), JsonSerializer.Serialize(Fields(dark)));
        Assert.Equal(JsonSerializer.Serialize(Fields(lightBase)), JsonSerializer.Serialize(Fields(light)));
        Assert.True(SheetStyle.Preset("joypro").IsDarkPage(false));
        Assert.False(SheetStyle.Preset("print").IsDarkPage(true));
        Assert.True(SheetStyle.Preset("dark-sheet").IsDarkPage(false));

        // Another table colour: the modifier column, the grid and the faint text follow it.
        SheetStyle green = SheetStyle.Preset("joypro");
        green.TableBackground = "#FF00FF00";
        SheetPalette p = green.Resolve(true);
        Assert.Equal(0xFF00FF00u, p.Table);
        Assert.NotEqual(darkBase.Mod, p.Mod);
        Assert.NotEqual(darkBase.Grid, p.Grid);
    }

    private static Dictionary<string, uint> Fields(SheetPalette p) =>
        typeof(SheetPalette).GetFields().ToDictionary(f => f.Name, f => (uint)f.GetValue(p)!);

    [Fact]
    public void Style_Json_RoundTrip_AndModifierText()
    {
        SheetStyle style = SheetStyle.Preset("high-contrast");
        style.FontFamily = "Consolas";
        style.FontSize = 21.5;
        style.Modifiers = SheetStyle.ParseModifierCsv("LAlt=#38BDF8, Shift A=#80F59E0B;bad;=x;RCtrl=nonsense");
        SheetStyle back = SheetStyle.Parse(style.ToJson())!;
        Assert.Equal(style.ToJson(), back.ToJson());
        Assert.Equal(2, back.Modifiers.Count);
        Assert.Equal("LAlt=#38BDF8,Shift A=#80F59E0B", back.ModifierCsv);
        Assert.Equal("#FF38BDF8", back.ModifierColor("LCtrl+LAlt"));
        Assert.Equal(string.Empty, back.ModifierColor("RShift"));
        Assert.Equal("#FF00B0FF", back.MarkerColor("axis"));
        Assert.Equal("#FFFF4081", back.MarkerColor("pov"));
        Assert.Equal("semibold", back.NormalizedWeight);
        Assert.Equal("#00000000", SheetStyle.NormalizeColor("none"));
        Assert.Equal("#FFAABBCC", SheetStyle.NormalizeColor("#ABC"));
        Assert.Equal(string.Empty, SheetStyle.NormalizeColor("#12"));
        // A sheet keeps its device's style in its JSON (what the engine reads and "Share layout..." carries).
        CalloutSheet sheet = new() { Width = 100, Height = 80, Style = style };
        CalloutSheet parsed = CalloutSheet.Parse(sheet.ToJson());
        Assert.NotNull(parsed.Style);
        Assert.Equal(style.ToJson(), parsed.Style!.ToJson());
        Assert.Null(CalloutSheet.Parse(new CalloutSheet { Width = 10, Height = 10 }.ToJson()).Style);
        Assert.Equal(style.ToJson(), parsed.Clone().Style!.ToJson());
    }

    [Fact]
    public void Arguments_NameEveryValue_TransparentIsNone()
    {
        SheetStyle style = SheetStyle.Preset("print");
        style.FontFamily = "Arial";
        List<string> args = MainWindowViewModel.SheetStyleArguments(style);
        string joined = string.Join(" ", args);
        foreach (string option in new[] { "--font", "--font-size", "--font-weight", "--text-color", "--outline-color", "--label-background", "--page-background",
                     "--table-background", "--table-border", "--header-background", "--header-text", "--table-text", "--line-color", "--marker-color-button",
                     "--marker-color-axis", "--marker-color-hat", "--marker-color-slider", "--modifier-colors" })
            Assert.Contains(option, args);
        Assert.Equal("none", args[args.IndexOf("--outline-color") + 1]);
        Assert.Equal("#FFE3E7ED", args[args.IndexOf("--header-background") + 1]);
        Assert.Contains("LAlt=#38BDF8", joined);
        // JoyPro's font: no --font (the tables keep Bahnschrift, a layout file its own font).
        Assert.DoesNotContain("--font", MainWindowViewModel.SheetStyleArguments(SheetStyle.Preset("joypro")));
    }

    // ------------------------------------------------------------------ the panel, persistence, presets

    [Fact]
    public void GlobalStyle_AndOwnPresets_SurviveARestart()
    {
        MainWindowViewModel first = new(new LayoutRedesignRenderTests.LayoutFakeEngine(Picture(), temp), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
        try
        {
            SheetStyleViewModel panel = first.CheatSheetStyleEditor;
            panel.HeaderBackground = "#FF123456";
            panel.TableText = "#FF654321";
            panel.FontSize = 22;
            panel.FontWeight = SheetStyleViewModel.WeightChoices[2];
            panel.AddModifierCommand.Execute(null);
            panel.Modifiers[^1].Name = "Pinky";
            panel.Modifiers[^1].Color = "#FFABCDEF";
            Assert.Equal("#FF123456", first.GlobalSheetStyle.HeaderBackground);
            // The Layout page's panel shows the same global style.
            Assert.Equal("#FF123456", first.SheetStyleEditor.HeaderBackground);
            Assert.Contains("Pinky=#ABCDEF", first.LayoutModifierColors);
            panel.NewPresetName = "My print";
            panel.SavePresetCommand.Execute(null);
            Assert.Contains(panel.Presets, p => p.IsUser && p.Id == "My print");
            Assert.Contains(first.SheetStyleEditor.Presets, p => p.IsUser && p.Id == "My print");
            Assert.True(File.Exists(Path.Combine(SheetStyleStore.PresetFolder, "My print.json")));
            panel.NewPresetName = "Print";
            panel.SavePresetCommand.Execute(null);
            Assert.Contains("JoyPro's presets", panel.Status);
        }
        finally
        {
            first.StopGameConfigWatcher();
        }
        Assert.True(File.Exists(SheetStyleStore.GlobalFile));

        MainWindowViewModel second = new(new LayoutRedesignRenderTests.LayoutFakeEngine(Picture(), temp), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
        try
        {
            SheetStyle style = second.GlobalSheetStyle;
            Assert.Equal("#FF123456", style.HeaderBackground);
            Assert.Equal("#FF654321", style.TableText);
            Assert.Equal(22, style.FontSize);
            Assert.Equal("bold", style.NormalizedWeight);
            Assert.Equal("#FFABCDEF", style.ModifierColor("Pinky"));
            SheetStyleViewModel panel = second.CheatSheetStyleEditor;
            Assert.Equal("#FF123456", panel.HeaderBackground);
            Assert.Equal(22, panel.FontSize);
            SheetStylePresetChoice mine = panel.Presets.Single(p => p.IsUser && p.Id == "My print");

            // A preset fills every value; the own one comes back as saved; deleting it removes the file.
            panel.SelectedPreset = panel.Presets.Single(p => p.Id == "print");
            panel.ApplyPresetCommand.Execute(null);
            Assert.Equal("#FFFFFFFF", second.GlobalSheetStyle.PageBackground);
            Assert.True(second.LayoutSheetLightStyle, "a white page is the engine's light style");
            Assert.Equal("print", DeviceReferenceService.ExportDrawingStyle.Preset);
            panel.SelectedPreset = mine;
            panel.ApplyPresetCommand.Execute(null);
            Assert.Equal("#FF123456", second.GlobalSheetStyle.HeaderBackground);
            Assert.False(second.LayoutSheetLightStyle);
            panel.DeletePresetCommand.Execute(null);
            Assert.DoesNotContain(panel.Presets, p => p.IsUser);
            Assert.False(File.Exists(Path.Combine(SheetStyleStore.PresetFolder, "My print.json")));
        }
        finally
        {
            second.StopGameConfigWatcher();
        }
    }

    [Fact]
    public void ModifierList_AddRemove_AndTheOldTextStillWorks()
    {
        MainWindowViewModel vm = new(new LayoutRedesignRenderTests.LayoutFakeEngine(Picture(), temp), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        try
        {
            SheetStyleViewModel panel = vm.CheatSheetStyleEditor;
            int count = panel.Modifiers.Count;
            Assert.True(count >= 5);
            panel.RemoveModifierCommand.Execute(panel.Modifiers[0]);
            Assert.Equal(count - 1, vm.GlobalSheetStyle.Modifiers.Count);
            vm.LayoutModifierColors = "LAlt=#38BDF8,RCtrl=#F59E0B";
            Assert.Equal(2, panel.Modifiers.Count);
            Assert.Equal("RCtrl", panel.Modifiers[1].Name);
            vm.LayoutTextColor = "#00FF00";
            Assert.Equal("#FF00FF00", panel.TextColor);
            vm.LayoutFontSize = "30";
            Assert.Equal(30, panel.FontSize);
            Assert.Equal(30, vm.LayoutEditorFontSizeValue);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    [Fact]
    public async Task CreateCheatSheets_PassesTheStyleToTheEngine()
    {
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(Picture(), temp);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        try
        {
            vm.ProfilePath = Path.Combine(temp, "style.pr0file");
            File.WriteAllText(vm.ProfilePath, "{}");
            vm.CheatSheetStyleEditor.SelectedPreset = vm.CheatSheetStyleEditor.Presets.Single(p => p.Id == "dark-sheet");
            vm.CheatSheetStyleEditor.ApplyPresetCommand.Execute(null);
            vm.CheatSheetStyleEditor.MarkerHat = "#FF112233";
            await vm.RenderLayoutCommand.ExecuteAsync(null);
            IReadOnlyList<string> call = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "layout", "render"));
            List<string> args = call.ToList();
            Assert.Equal("#FF334155", args[args.IndexOf("--header-background") + 1]);
            Assert.Equal("#FF1E293B", args[args.IndexOf("--table-background") + 1]);
            Assert.Equal("#FF112233", args[args.IndexOf("--marker-color-hat") + 1]);
            Assert.Equal("dark", args[args.IndexOf("--style") + 1]);
            Assert.Contains("--drawing-style", args);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // ------------------------------------------------------------------ a device's own style

    [Fact]
    public void DeviceStyle_IsKeptInItsSheet_ComesBack_AndIsRemovedAgain()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(OpenStickAsync());
            try
            {
                Assert.True(vm.LayoutShowsCallouts, "the stick's sheet is shown");
                SheetStyleViewModel panel = vm.SheetStyleEditor;
                Assert.True(panel.ShowOwnStyleToggle);
                Assert.False(panel.UsesOwnStyle);
                panel.UsesOwnStyle = true;
                Assert.True(vm.LayoutUsesOwnSheetStyle);
                panel.HeaderBackground = "#FFAA0011";
                // The global style is untouched, the shown sheet draws with the device's.
                Assert.NotEqual("#FFAA0011", vm.GlobalSheetStyle.HeaderBackground);
                Assert.Equal("#FFAA0011", vm.LayoutSheetStyle.HeaderBackground);
                Assert.Equal("#FFAA0011", vm.LayoutCallouts.Sheet!.Style!.HeaderBackground);
                string file = DeviceReferenceService.CalloutFileOf(AxisTuningTests.Stick)!;
                Assert.NotNull(file);
                DeviceLayoutSet saved = DeviceLayoutSet.Parse(File.ReadAllText(file), file);
                Assert.Equal("#FFAA0011", saved.Callouts!.Style!.HeaderBackground);

                // The cheat sheet panel edits the global style; the device keeps its own.
                vm.CheatSheetStyleEditor.HeaderBackground = "#FF00AA11";
                Assert.Equal("#FFAA0011", vm.LayoutSheetStyle.HeaderBackground);
                Assert.Equal("#FFAA0011", panel.HeaderBackground);

                // Off: the sheet file loses the style, the global one is shown again.
                panel.UsesOwnStyle = false;
                Assert.False(vm.LayoutUsesOwnSheetStyle);
                Assert.Equal("#FF00AA11", panel.HeaderBackground);
                saved = DeviceLayoutSet.Parse(File.ReadAllText(file), file);
                Assert.Null(saved.Callouts!.Style);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    [Fact]
    public void DeviceStyle_TravelsWithShareLayout_AndComesBackOnImport()
    {
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        AvaloniaRenderThread.Run(() =>
        {
            const string model = "winwing-f18-takeoff-panel";
            DeviceReferenceViewModel reference = new() { VendorPhotosEnabled = false };
            Assert.True(AvaloniaRenderThread.Wait(ShowAsync(reference, model)));
            SheetStyle own = SheetStyle.Preset("dark-sheet");
            own.HeaderBackground = "#FF13579B";
            own.Modifiers = SheetStyle.ParseModifierCsv("Shift A=#FF2468AC");
            CalloutSheet sheet = new()
            {
                Width = 400, Height = 300, PictureX = 100, PictureY = 20, PictureWidth = 200, PictureHeight = 260, Style = own
            };
            reference.CurrentCallouts = () => sheet;
            string shared = Path.Combine(temp, "shared.layout.json");
            reference.ExportLayout(shared);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(shared)))
            {
                JsonElement style = json.RootElement.GetProperty("callouts").GetProperty("style");
                Assert.Equal("#FF13579B", style.GetProperty("headerBackground").GetString());
                Assert.Equal("joypro-sheet-style", style.GetProperty("format").GetString());
            }

            // Another PC: no sheet of the model; the import brings the device's style back with its sheet.
            if (Directory.Exists(DeviceReferenceService.CalloutFolder()))
                Directory.Delete(DeviceReferenceService.CalloutFolder(), recursive: true);
            Assert.True(AvaloniaRenderThread.Wait(reference.ImportLayoutAsync(shared)));
            DeviceLayoutSet exported = DeviceLayoutSet.Parse(File.ReadAllText(shared), shared);
            DeviceLayoutSet? back = DeviceReferenceService.LoadCallouts(model, exported.PhotoKind == "drawing" ? "schematic" : exported.PhotoSha256);
            Assert.NotNull(back?.Callouts?.Style);
            Assert.Equal("#FF13579B", back!.Callouts!.Style!.HeaderBackground);
            Assert.Equal("#FF2468AC", back.Callouts.Style.ModifierColor("Shift A"));
            Assert.Equal(own.ToJson(), back.Callouts.Style.ToJson());
        });
    }

    private static async Task<bool> ShowAsync(DeviceReferenceViewModel reference, string model)
    {
        await reference.ShowModelAsync(model);
        return reference.Model?.Id == model;
    }

    // ------------------------------------------------------------------ what is drawn

    [Fact]
    public void Engine_DrawsTheSheetInTheChosenColours()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string png = Path.Combine(temp, "styled.png"), json = Path.Combine(temp, "styled.json");
        (int exit, string output) = Run(engine, ["layout", "callouts", "--model", "thrustmaster-warthog-stick", "--sample", "--png", png, "--save", json, "--print-width", "0",
            "--page-background", "#0000FF", "--table-background", "#00FF00", "--header-background", "#FF0000", "--table-border", "#FFFF00", "--line-color", "#FF00FF"]);
        Assert.True(exit == 0, output);
        CalloutSheet sheet = DeviceLayoutSet.Parse(File.ReadAllText(json), json).Callouts!;
        using SKBitmap bitmap = SKBitmap.Decode(png);
        CalloutTable table = sheet.Tables.First(t => t.VisibleRows().Count >= 2 && !t.IsGrid);
        double[] r = sheet.RectOf(table);
        double rh = sheet.RowHeight, th = sheet.TitleHeight;
        AssertPixel(bitmap, 3, 3, 0, 0, 255, "page background");
        // The title row right of its text, the first row's icon cell away from the icon.
        AssertPixel(bitmap, (int)(r[0] + r[2] - rh * 0.4), (int)(r[1] + th * 0.5), 255, 0, 0, "header row");
        AssertPixel(bitmap, (int)(r[0] + rh * 0.12), (int)(r[1] + th + rh * 0.12), 0, 255, 0, "table background");

        // Wrong colours are refused with the option's name.
        (int bad, string badOutput) = Run(engine, ["layout", "callouts", "--model", "thrustmaster-warthog-stick", "--sample", "--png", png, "--header-background", "rot"]);
        Assert.NotEqual(0, bad);
        Assert.Contains("--header-background", badOutput);
        // A preset by name.
        (int preset, string presetOutput) = Run(engine, ["layout", "callouts", "--model", "thrustmaster-warthog-stick", "--sample", "--png", png, "--print-width", "0", "--sheet-style", "print"]);
        Assert.True(preset == 0, presetOutput);
        using SKBitmap white = SKBitmap.Decode(png);
        AssertPixel(white, 3, 3, 255, 255, 255, "the print preset's page");
    }

    [Fact]
    public void Preview_DrawsMarkersHeaderAndTableInTheStyle()
    {
        AvaloniaRenderThread.Run(() =>
        {
            SheetStyle style = SheetStyle.Preset("joypro");
            style.MarkerButton = "#FFFF0000";
            style.HeaderBackground = "#FF00FF00";
            style.TableBackground = "#FF0000FF";
            SheetStylePreview preview = new() { StyleValue = style, Width = 400, Height = 176 };
            using RenderTargetBitmap bitmap = RenderControl(preview, 400, 176);
            using SKBitmap pixels = ToSkia(bitmap);
            double pictureWidth = Math.Max(60, 400 * 0.36);
            // The button marker (the first dot), the header row (right end), the table (left of the modifier column).
            AssertPixel(pixels, 28, 30, 255, 0, 0, "button marker");
            double tx = 10 + pictureWidth + 18, tw = 400 - tx - 10, rowH = 22, titleH = Math.Round(rowH * 0.9), top = Math.Max(10, (176 - (titleH + 3 * rowH)) / 2);
            double modW = Math.Round(tw * 0.34);
            AssertPixel(pixels, (int)(tx + tw - modW - 6), (int)(top + 3), 0, 255, 0, "header row");
            AssertPixel(pixels, (int)(tx + tw - modW - 6), (int)(top + titleH + rowH * 1.5), 0, 0, 255, "table background");
        });
    }

    // ------------------------------------------------------------------ layout of the panel and the page

    [Fact]
    public void Panel_AndLayoutPage_HaveNoOverlaps_AtBothSizes_BigFonts_AndThemes()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_SHEET_STYLE_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(OpenStickAsync());
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((string preset, double sheetFont, double uiScale) in new[] { ("joypro", 16.0, 1.0), ("print", 72.0, 1.0), ("dark-sheet", 40.0, 1.5) })
                    {
                        vm.CheatSheetStyleEditor.SelectedPreset = vm.CheatSheetStyleEditor.Presets.Single(p => p.Id == preset);
                        vm.CheatSheetStyleEditor.ApplyPresetCommand.Execute(null);
                        vm.CheatSheetStyleEditor.FontSize = sheetFont;
                        ThemeService.ApplyTypography(uiScale, null);
                        foreach ((double width, double height) in new[] { (1280.0, 800.0), (2267.0, 1250.0) })
                        {
                            string label = theme + " " + preset + " " + width + (uiScale > 1 ? " text" + uiScale : string.Empty);
                            CheckPage(vm, width, height, label, cheatSheets: false, problems, outDir);
                            CheckPage(vm, width, height, label, cheatSheets: true, problems, outDir);
                        }
                    }
                }
            }
            finally
            {
                ThemeService.ApplyTypography(1.0, null);
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    private static void CheckPage(MainWindowViewModel vm, double width, double height, string label, bool cheatSheets, List<string> problems, string? outDir)
    {
        vm.IsCheatSheetsSection = cheatSheets;
        AvaloniaRenderThread.Pump();
        using HiddenWindow window = GateRender.NewWindow(vm, width, height);
        Control root = GateRender.Root(window);
        string expanderName = cheatSheets ? "CheatSheetStyleExpander" : "LayoutSheetStyleExpander";
        Expander? expander = root.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == expanderName);
        if (expander == null)
        {
            problems.Add(label + ": the sheet style panel is not on the page (" + expanderName + ")");
            return;
        }
        expander.IsExpanded = true;
        window.Settle();
        SheetStyleEditor? editor = expander.GetVisualDescendants().OfType<SheetStyleEditor>().FirstOrDefault();
        if (editor == null || editor.Bounds.Width < 200)
        {
            problems.Add(label + ": the sheet style panel has no room (" + (editor?.Bounds.Width ?? 0) + " px)");
            return;
        }
        string page = cheatSheets ? "cheatsheets" : "layout";
        // The part on screen in the page, then the whole panel at its width (scrolled out of view parts too).
        foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height, editor))
            problems.Add(label + " " + page + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b) + " at " + ra + " / " + rb);
        foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, editor))
            problems.Add(label + " " + page + ": '" + text.Text + "' cut off (needs " + need.ToString("0") + ", has " + got.ToString("0") + ")");
        if (!string.IsNullOrWhiteSpace(outDir))
            GateRender.Save(window, width, height, 1, Path.Combine(outDir, "sheet-style-" + page + "-" + label.Replace(' ', '-') + ".png"));
        CheckStandalonePanel(cheatSheets ? vm.CheatSheetStyleEditor : vm.SheetStyleEditor, editor.Bounds.Width, label + " " + page + " panel", problems,
            string.IsNullOrWhiteSpace(outDir) ? null : Path.Combine(outDir, "sheet-style-panel-" + page + "-" + label.Replace(' ', '-') + ".png"));
    }

    // The whole panel at the width it has on the page (every row visible).
    private static void CheckStandalonePanel(SheetStyleViewModel panel, double width, string label, List<string> problems, string? file)
    {
        SheetStyleEditor editor = new() { DataContext = panel, Width = width, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        Border host = new() { Child = editor, Padding = new Thickness(0) };
        Window window = new() { Content = host, RequestedThemeVariant = Application.Current!.ActualThemeVariant };
        double height = 2600;
        using HiddenWindow view = HiddenWindow.Create(window, new Size(width, height));
        view.Settle();
        height = Math.Ceiling(editor.Bounds.Height) + 4;
        Control root = view.Page;
        foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height, editor))
            problems.Add(label + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b) + " at " + ra + " / " + rb);
        foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, editor))
            problems.Add(label + ": '" + text.Text + "' cut off (needs " + need.ToString("0") + ", has " + got.ToString("0") + ")");
        foreach (Control c in editor.GetVisualDescendants().OfType<Control>().Where(c => c is Button or TextBox or ComboBox or NumericUpDown or ColorField && c.IsEffectivelyVisible))
        {
            Rect? r = GateRender.BoundsInWindow(c, root);
            if (r != null && r.Value.Right > width + 1)
                problems.Add(label + ": " + LayoutChecks.Describe(c) + " runs out of the panel (" + r.Value.Right.ToString("0") + " > " + width.ToString("0") + ")");
        }
        if (file != null)
            GateRender.Save(view, width, Math.Min(height, 2600), 1, file);
    }

    // ------------------------------------------------------------------ helpers

    // The Layout page with the Warthog stick's sheet (tables and lines on JoyPro's drawing).
    private async Task<MainWindowViewModel> OpenStickAsync()
    {
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(Picture(), temp);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string profile = Path.Combine(temp, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Layout;
        vm.IsCheatSheetsSection = false;
        if (vm.LayoutSuggestions.Count == 0)
            await vm.SuggestLayoutsCommand.ExecuteAsync(null);
        vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.FirstOrDefault(s => s.Device == AxisTuningTests.Stick);
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.DeviceReference.DeviceName == AxisTuningTests.Stick && vm.HasReferenceModel);
        vm.LayoutShowsLayoutFile = false;
        vm.LayoutCalloutsEnabled = true;
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.LayoutShowsCallouts);
        return vm;
    }

    private string Picture()
    {
        string file = Path.Combine(temp, "stand-in.png");
        if (File.Exists(file))
            return file;
        using SKBitmap bitmap = new(400, 600);
        bitmap.Erase(new SKColor(95, 95, 95));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(file, data.ToArray());
        return file;
    }

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private (int Exit, string Output) Run(string engine, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(temp, "engine-data");
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120000);
        return (process.ExitCode, output.Result + errors.Result);
    }

    private static void AssertPixel(SKBitmap bitmap, int x, int y, byte r, byte g, byte b, string what)
    {
        SKColor c = bitmap.GetPixel(Math.Clamp(x, 0, bitmap.Width - 1), Math.Clamp(y, 0, bitmap.Height - 1));
        Assert.True(Math.Abs(c.Red - r) <= 12 && Math.Abs(c.Green - g) <= 12 && Math.Abs(c.Blue - b) <= 12,
            what + " at " + x + "," + y + " is " + c + ", expected #" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2"));
    }

    private static RenderTargetBitmap RenderControl(Control control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(control);
        return bitmap;
    }

    private static SKBitmap ToSkia(RenderTargetBitmap bitmap)
    {
        using MemoryStream stream = new();
        bitmap.Save(stream);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
}
