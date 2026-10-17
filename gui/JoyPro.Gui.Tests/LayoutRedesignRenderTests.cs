using System;
using System.Collections.Generic;
using System.Globalization;
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
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Layout pages (device picture editor, bind on the picture, cheat sheets) rendered offscreen as the whole window,
// at 1920x1080 and 1280x800, in the dark and the light theme. The device picture is a JoyPro layout of a VKB Gunfighter
// (47 markers); the picture file comes from JOYPRO_UI_LAYOUT_PICTURE (a PNG, e.g. the preview "engine layout
// template-inspect" writes), else a drawn stand-in. Needs the whole application, so it renders only when
// JOYPRO_UI_DESIGN_OUT names an output folder, and must then run alone:
//   set JOYPRO_UI_DESIGN_OUT=<repo>\docs\ui & set JOYPRO_UI_DESIGN_TAG=after & dotnet test gui/JoyPro.Gui.Tests --filter LayoutRedesignRenderTests
// Files: layout-<page>-<tag>-<width>-<theme>.png.
[SupportedOSPlatform("windows")]
public sealed class LayoutRedesignRenderTests
{
    private const string Stick = AxisTuningTests.Stick;

    [Fact]
    public void RenderLayoutPages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        string tag = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_TAG") is { Length: > 0 } t ? t : "after";
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ui-layout");
        Directory.CreateDirectory(work);

        AvaloniaRenderThread.Run(() =>
        {
            string picture = ResolvePicture(work);
            LayoutFakeEngine fake = new(picture, work);
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLoadedVmAsync(fake, work));
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1920, 1080), (1280, 800) })
                    {
                        foreach (string page in LayoutPages.Pages)
                        {
                            AvaloniaRenderThread.Wait(LayoutPages.ShowAsync(vm, page));
                            AvaloniaRenderThread.Pump();
                            int pageHeight = LayoutPages.Height(page, height);
                            RenderWindow(vm, width, pageHeight, Path.Combine(outDir, "layout-" + page + "-" + tag + "-" + width + "-" + theme + ".png"));
                        }
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    private static async Task<MainWindowViewModel> CreateLoadedVmAsync(LayoutFakeEngine fake, string work)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        await vm.StartGameConfigWatcherAsync();
        vm.LayoutExportPath = Path.Combine(work, "Cheat sheets");
        vm.StatusText = "Ready.";
        return vm;
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
    }

    // The pages and how to reach them. The "before" pictures (docs/ui/layout-*-before-*.png) were rendered with the first
    // version of this test on the old GUI (Layout Studio; Visual assign inside Bind Studio; the layout PNG export at the
    // end of the Export page, rendered 2x+900 px high to reach it); since the redesign only "after" renders.
    private const string Throttle = "Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}";
    private const string CockpitBox = "Home Cockpit Box {11111111-0000-0000-0000-000000000001}";

    // The pages and how to reach them. The "before" pictures (docs/ui/layout-*-before-*.png) were rendered with the first
    // version of this test on the old GUI (Layout Studio; Visual assign inside Bind Studio; the layout PNG export at the
    // end of the Export page, rendered 2x+900 px high to reach it); since the redesign only "after" renders.
    // The stick shows its device reference (JoyPro's drawing of the Warthog stick) unless "layoutfile" picks its layout
    // file (a VKB layout of the user); "assistant" is an unknown button box with the marker assistant waiting.
    private static class LayoutPages
    {
        // "tables*": the stick's reference picture as a callout sheet (tables and lines, JoyPro 1's look); the
        // marker pages show the markers with labels (Tables off).
        public static readonly string[] Pages = ["editor", "view", "bind", "tables", "tables-edit", "tables-preview", "layoutfile", "assistant", "visualbind", "cheatsheets", "empty"];

        public static int Height(string page, int height) => height;

        public static async Task ShowAsync(MainWindowViewModel vm, string page)
        {
            vm.CloseReferenceAssistantCommand.Execute(null);
            vm.LayoutCalloutsEnabled = page.StartsWith("tables", StringComparison.Ordinal);
            vm.SelectedCalloutPreview = MainWindowViewModel.RelationNamesPreview;
            switch (page)
            {
                case "tables":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.View;
                    SelectPart(vm, "JOY_BTN1");
                    break;
                case "tables-edit":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.EditMarkers;
                    if (vm.LayoutCallouts.Sheet?.TableOfInput("JOY_BTN7") is { } tms)
                        vm.LayoutCallouts.SelectRow(tms, 0, false);
                    break;
                case "tables-preview":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.View;
                    await WaitForAsync(() => vm.CalloutPreviewOptions.Count > 1);
                    vm.SelectedCalloutPreview = vm.CalloutPreviewOptions.FirstOrDefault(o => o.Contains("F-16C_50", StringComparison.Ordinal)) ?? MainWindowViewModel.RelationNamesPreview;
                    break;
                case "editor":
                    vm.OpenVisualAssignMode = false;
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.EditMarkers;
                    SelectPart(vm, "TMS");
                    break;
                case "view":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.View;
                    SelectPart(vm, "JOY_BTN1");
                    break;
                case "bind":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: false);
                    vm.LayoutCanvasMode = DeviceCanvasMode.Bind;
                    SelectPart(vm, "JOY_BTN2");
                    break;
                case "layoutfile":
                    await OpenOnLayoutPageAsync(vm, Stick, layoutFile: true);
                    vm.LayoutCanvasMode = DeviceCanvasMode.EditMarkers;
                    vm.SelectedLayoutEditorMarker = vm.LayoutEditorMarkers.FirstOrDefault(m => m.Token == "JOY_BTN_POV2_U");
                    break;
                case "assistant":
                    vm.CurrentStage = UiStage.Layout;
                    vm.IsCheatSheetsSection = false;
                    await vm.ShowWorkspaceDeviceAsync(CockpitBox);
                    await WaitForAsync(() => vm.DeviceReference.DeviceName == CockpitBox && vm.HasReferenceModel);
                    vm.OpenReferenceAssistantCommand.Execute(null);
                    vm.DeviceReference.Assistant?.Found("JOY_BTN5");
                    break;
                case "visualbind":
                    vm.CurrentStage = UiStage.Binds;
                    vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Weapon Release");
                    vm.OpenVisualAssignMode = true;
                    vm.VisualAssignDevice = Stick;
                    await vm.LoadVisualAssignCommand.ExecuteAsync(null);
                    vm.LayoutShowsLayoutFile = false;
                    await WaitForAsync(() => vm.HasReferenceModel);
                    SelectPart(vm, "JOY_BTN7");
                    break;
                case "cheatsheets":
                    vm.OpenVisualAssignMode = false;
                    vm.CurrentStage = UiStage.Layout;
                    vm.IsCheatSheetsSection = true;
                    if (vm.CheatSheetPreviews.Count == 0)
                    {
                        foreach (SelectableTagItem game in vm.LayoutGameOptions.Where(g => g.Name.Contains("DCS", StringComparison.OrdinalIgnoreCase)))
                            game.IsSelected = true;
                        await vm.RenderLayoutCommand.ExecuteAsync(null);
                    }
                    break;
                case "empty":
                    vm.CurrentStage = UiStage.Layout;
                    vm.IsCheatSheetsSection = false;
                    vm.LayoutCanvasMode = DeviceCanvasMode.View;
                    vm.SelectedLayoutSuggestion = null;
                    vm.ClearLayoutWorkspace();
                    break;
            }
        }

        private static async Task OpenOnLayoutPageAsync(MainWindowViewModel vm, string device, bool layoutFile)
        {
            vm.CurrentStage = UiStage.Layout;
            vm.IsCheatSheetsSection = false;
            if (vm.LayoutSuggestions.Count == 0)
                await vm.SuggestLayoutsCommand.ExecuteAsync(null);
            vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.FirstOrDefault(s => s.Device == device);
            await WaitForAsync(() => vm.LayoutEditorMarkers.Count > 0 && vm.DeviceReference.DeviceName == device && vm.HasReferenceModel);
            vm.ApplySelectedLayoutSuggestionsCommand.Execute(null);
            vm.LayoutShowsLayoutFile = layoutFile;
        }

        private static void SelectPart(MainWindowViewModel vm, string input)
        {
            // An input ("JOY_BTN2") or a word of the part's name ("TMS").
            ReferenceLegendRow? row = vm.DeviceReference.Legend.FirstOrDefault(r => r.Input == input) ??
                                      vm.DeviceReference.Legend.FirstOrDefault(r => r.Name.Contains(input, StringComparison.OrdinalIgnoreCase));
            if (row != null)
                vm.DeviceReference.SelectedLegendRow = row;
        }
    }
    private static string ResolvePicture(string work)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("JOYPRO_UI_LAYOUT_PICTURE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;
        string file = Path.Combine(work, "stand-in-device.png");
        using RenderTargetBitmap bitmap = new(new PixelSize(1627, 2583));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#5F5F5F")), new Rect(0, 0, 1627, 2583));
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#2B2B2B")), null, new Rect(640, 300, 320, 1500), 120, 120);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#1F1F1F")), null, new Point(800, 360), 220, 160);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#2B2B2B")), null, new Rect(600, 1900, 420, 560), 60, 60);
        }
        bitmap.Save(file);
        return file;
    }

    private static void RenderWindow(MainWindowViewModel vm, int width, int height, string file)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? page2) && page2 is IBrush own
            ? own
            : new SolidColorBrush(main.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    // The design profile of UiDesignRenderTests plus the layout commands: a layout for the stick (VKB Gunfighter
    // markers), none for the throttle, and "layout render" writing cheat sheets (copies of the picture).
    internal sealed class LayoutFakeEngine : IEngineCliClient
    {
        private readonly UiDesignRenderTests.DesignFakeEngine design = new();
        private readonly string picture;
        private readonly string layoutFile;

        public LayoutFakeEngine(string picture, string work)
        {
            this.picture = picture;
            layoutFile = Path.Combine(work, "VKB_GunfighterMK3.layout");
            if (!File.Exists(layoutFile))
                File.WriteAllText(layoutFile, "stand-in");
        }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string? output = LayoutOutput(arguments);
            if (output == null)
                return design.RunAsync(engineExePath, arguments, cancellationToken);
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private string? LayoutOutput(IReadOnlyList<string> args)
        {
            if (AxisTuningTests.Seq(args, "hud", "devices"))
            {
                return "{\"Devices\":[" +
                       "{\"Device\":" + Json(Stick) + ",\"Name\":\"Joystick - HOTAS Warthog\",\"VidPid\":\"044F:0402\",\"ModelId\":\"thrustmaster-warthog-stick\",\"Kind\":\"stick\",\"Buttons\":19,\"Hats\":4,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"}],\"Image\":\"\"}," +
                       "{\"Device\":" + Json(Throttle) + ",\"Name\":\"Throttle - HOTAS Warthog\",\"VidPid\":\"044F:0404\",\"ModelId\":\"thrustmaster-hotas-warthog-throttle\",\"Kind\":\"throttle\",\"Buttons\":32,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_Z\"},{\"Input\":\"JOY_RZ\"}],\"Image\":\"\"}," +
                       "{\"Device\":" + Json(CockpitBox) + ",\"Name\":\"Home Cockpit Box\",\"VidPid\":\"1209:0001\",\"ModelId\":\"\",\"Generic\":true,\"Kind\":\"panel\",\"Buttons\":12,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"}],\"Image\":\"\"}" +
                       "]}";
            }
            if (AxisTuningTests.Seq(args, "layout", "suggest"))
            {
                string stick = "{\"Device\":" + Json(Stick) + ",\"TemplateName\":\"VKB Gunfighter MK3\",\"LayoutPath\":" + Json(layoutFile) +
                               ",\"Score\":92,\"MarkerCount\":47,\"MatchReason\":\"Same product (VID:PID 231D:0127)\",\"Source\":\"catalog\",\"Model\":\"VKB Gladiator / Gunfighter\"}";
                string throttle = "{\"Device\":\"Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}\",\"TemplateName\":\"\",\"LayoutPath\":\"\",\"Score\":0,\"MarkerCount\":0," +
                                  "\"MatchReason\":\"No picture of this model yet\",\"Source\":\"\",\"Model\":\"Thrustmaster HOTAS Warthog Throttle\"}";
                bool onlyStick = args.Contains("--device");
                return "{\"Suggestions\":[" + stick + (onlyStick ? string.Empty : "," + throttle) + "]}";
            }
            if (AxisTuningTests.Seq(args, "layout", "titles"))
            {
                // The F-16's own menu titles of the stick's binds (the sheet's preview).
                return "{\"Device\":" + Json(Stick) + ",\"Modifier\":\"Shift\",\"Targets\":[" +
                       "{\"Game\":\"DCS\",\"Plane\":\"F-16C_50\",\"Titles\":{\"JOY_BTN1\":\"Gun Trigger - Second Detent\",\"JOY_BTN2\":\"Weapon Release\"," +
                       "\"JOY_BTN_POV1_U\":\"Trim Nose Down\",\"JOY_BTN_POV1_D\":\"Trim Nose Up\",\"Shift+JOY_BTN2\":\"Pickle - Jettison\",\"JOY_BTN7\":\"TMS Up\"}}," +
                       "{\"Game\":\"DCS\",\"Plane\":\"A-10C\",\"Titles\":{\"JOY_BTN1\":\"Gun Fire\"}}]}";
            }
            if (AxisTuningTests.Seq(args, "layout", "template-inspect"))
            {
                return "{\"SourcePath\":" + Json(layoutFile) + ",\"PreviewImagePath\":" + Json(picture) + ",\"Width\":1627,\"Height\":2583,\"MarkerCount\":47," +
                       "\"Markers\":" + GunfighterMarkers + ",\"TableCount\":0,\"Tables\":[],\"ConnectorCount\":0,\"Connectors\":[],\"Warnings\":[],\"Errors\":[]}";
            }
            if (AxisTuningTests.Seq(args, "layout", "render"))
            {
                int path = args.ToList().IndexOf("--path");
                string folder = path >= 0 && path + 1 < args.Count ? args[path + 1] : Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ui-layout", "Cheat sheets");
                List<string> written = [];
                foreach (string name in new[] { "Gunfighter.png", Path.Combine("DCS", "F-16C_50", "Gunfighter.png"), Path.Combine("DCS", "A-10C", "Gunfighter.png"), Path.Combine("DCS", "FA-18C_hornet", "Gunfighter.png") })
                {
                    string target = Path.Combine(folder, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(picture))
                        File.Copy(picture, target, overwrite: true);
                    else
                        File.WriteAllBytes(target, [1, 2, 3]);
                    written.Add(target);
                }
                return "Layout Render" + Environment.NewLine +
                       "Export Path: " + folder + Environment.NewLine +
                       "Total Images Written: " + written.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                       "Written Files: " + written.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                       string.Join(Environment.NewLine, written.Select(w => "  - " + w)) + Environment.NewLine;
            }
            return null;
        }

        private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

        private const string GunfighterMarkers = """
[{"Token":"JOY_BTN23","Type":"button","Modifier":"","X":584.1,"Y":6.7},{"Token":"JOY_BTN_POV1_U","Type":"pov","Modifier":"","X":575.7,"Y":65.5},{"Token":"JOY_BTN_POV1_R","Type":"pov","Modifier":"","X":575.7,"Y":124.4},{"Token":"JOY_BTN_POV1_L","Type":"pov","Modifier":"","X":584.1,"Y":183.2},{"Token":"JOY_BTN_POV1_D","Type":"pov","Modifier":"","X":577.4,"Y":238.6},{"Token":"JOY_RX","Type":"axis","Modifier":"","X":579.1,"Y":295.8},{"Token":"JOY_RY","Type":"axis","Modifier":"","X":582.4,"Y":354.6},{"Token":"JOY_BTN4","Type":"button","Modifier":"","X":88.3,"Y":425.2},{"Token":"JOY_BTN5","Type":"button","Modifier":"","X":1079.9,"Y":448.7},{"Token":"JOY_BTN22","Type":"button","Modifier":"","X":85.0,"Y":484.0},{"Token":"JOY_BTN30","Type":"button","Modifier":"","X":1083.2,"Y":526.0},{"Token":"JOY_BTN_POV2_U","Type":"pov","Modifier":"","X":86.7,"Y":539.5},{"Token":"JOY_BTN19","Type":"button","Modifier":"","X":1088.3,"Y":581.5},{"Token":"JOY_BTN_POV2_R","Type":"pov","Modifier":"","X":88.3,"Y":598.3},{"Token":"JOY_BTN20","Type":"button","Modifier":"","X":1088.3,"Y":638.6},{"Token":"JOY_BTN_POV2_L","Type":"pov","Modifier":"","X":93.4,"Y":655.4},{"Token":"JOY_BTN31","Type":"button","Modifier":"","X":1089.9,"Y":697.4},{"Token":"JOY_BTN_POV2_D","Type":"pov","Modifier":"","X":85.0,"Y":712.6},{"Token":"JOY_BTN21","Type":"button","Modifier":"","X":1091.6,"Y":757.9},{"Token":"JOY_RZ","Type":"axis","Modifier":"","X":85.0,"Y":771.4},{"Token":"JOY_SLIDER1","Type":"slider","Modifier":"","X":81.6,"Y":826.8},{"Token":"JOY_BTN6","Type":"button","Modifier":"","X":1084.9,"Y":835.2},{"Token":"JOY_BTN18","Type":"button","Modifier":"","X":76.6,"Y":892.4},{"Token":"JOY_BTN14","Type":"button","Modifier":"","X":81.6,"Y":949.5},{"Token":"JOY_BTN15","Type":"button","Modifier":"","X":88.3,"Y":1005.0},{"Token":"JOY_BTN17","Type":"button","Modifier":"","X":88.3,"Y":1063.8},{"Token":"JOY_BTN16","Type":"button","Modifier":"","X":83.3,"Y":1120.9},{"Token":"JOY_BTN9","Type":"button","Modifier":"","X":548.8,"Y":1233.5},{"Token":"JOY_BTN10","Type":"button","Modifier":"Pinky","X":550.5,"Y":1287.3},{"Token":"JOY_BTN11","Type":"button","Modifier":"","X":548.8,"Y":1349.5},{"Token":"JOY_BTN13","Type":"button","Modifier":"","X":543.8,"Y":1404.9},{"Token":"JOY_BTN12","Type":"button","Modifier":"","X":543.8,"Y":1460.4},{"Token":"JOY_BTN40","Type":"button","Modifier":"","X":86.7,"Y":1700.7},{"Token":"JOY_BTN43","Type":"button","Modifier":"","X":93.4,"Y":1756.2},{"Token":"JOY_BTN41","Type":"button","Modifier":"","X":98.4,"Y":1816.7},{"Token":"JOY_BTN24","Type":"button","Modifier":"","X":90.0,"Y":1872.1},{"Token":"JOY_BTN42","Type":"button","Modifier":"","X":93.4,"Y":1932.6},{"Token":"JOY_BTN1","Type":"button","Modifier":"","X":79.9,"Y":2023.4},{"Token":"JOY_BTN2","Type":"button","Modifier":"","X":81.6,"Y":2083.9},{"Token":"JOY_SLIDER2","Type":"slider","Modifier":"","X":93.4,"Y":2188.1},{"Token":"JOY_BTN7","Type":"button","Modifier":"","X":1088.3,"Y":2203.2},{"Token":"JOY_BTN29","Type":"button","Modifier":"","X":95.1,"Y":2240.2},{"Token":"JOY_BTN28","Type":"button","Modifier":"","X":93.4,"Y":2293.9},{"Token":"JOY_BTN3","Type":"button","Modifier":"","X":90.0,"Y":2346.0},{"Token":"JOY_Z","Type":"axis","Modifier":"","X":1084.9,"Y":2346.0},{"Token":"JOY_Y","Type":"axis","Modifier":"","X":708.5,"Y":2488.9},{"Token":"JOY_X","Type":"axis","Modifier":"","X":91.7,"Y":2492.3}]
""";
    }
}
