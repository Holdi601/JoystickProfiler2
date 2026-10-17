using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders the force feedback graph editor (canvas and Bezier curve editor) with the built-in flight graph into PNG
// files without a window: %TEMP%\JoyPro2.Gui.Tests\ffb-editor, or the folder in JOYPRO_FFB_EDITOR_PREVIEW.
// Only Skia is set up (like AxisCurveRenderTests), the controls draw themselves.
[SupportedOSPlatform("windows")]
public sealed class FfbGraphEditorRenderTests
{
    public static string OutputFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_FFB_EDITOR_PREVIEW");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ffb-editor")
                : configured;
        }
    }

    [Fact]
    public void RenderBuiltInGraph()
    {
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        // On the one Avalonia thread of the test run (rendering from the test thread while Avalonia lives on the render
        // thread crashed the test host now and then).
        AvaloniaRenderThread.Run(() =>
        {
            FfbGraphEditorViewModel editor = new();
            FfbGraph graph = editor.Graph;
            CurveNode curve = graph.Nodes.OfType<CurveNode>().First();
            editor.SelectedNode = curve;

            // Live values as the running process would send them (some negative, some none).
            Dictionary<string, double> values = [];
            int i = 0;
            foreach (FfbNode node in graph.Nodes.Where(n => !n.IsSink))
                values[node.Id] = (i++ % 5 - 1) * 0.3;
            editor.SetLiveValues(values);

            string canvasFile = Path.Combine(outDir, "ffb-graph-editor.png");
            FfbGraphCanvas canvas = new() { Editor = editor };
            Render(canvas, new Size(1500, 820), canvasFile);
            Assert.True(canvas.Zoom >= 0.15 && canvas.Zoom <= 1, "zoom " + canvas.Zoom);

            // The curve editor of the selected Curve node, with a middle point selected.
            editor.AddCurvePoint(0.7, 0.35);
            string curveFile = Path.Combine(outDir, "ffb-curve-editor.png");
            Render(new FfbCurveEditor { Editor = editor }, new Size(320, 210), curveFile);

            // A small graph at 100 %: telemetry -> curve -> group (multi input) -> periodic effect, a wire selected.
            FfbGraphEditorViewModel small = new();
            small.SetGraph(new FfbGraph { Name = "small" }, string.Empty);
            FfbNode aoa = small.AddNode(NodeKind.Telemetry, 0, 0);
            ((TelemetryNode)aoa).Key = "ac.aoa";
            FfbNode constant = small.AddNode(NodeKind.Constant, 0, 140);
            FfbNode shaping = small.AddNode(NodeKind.Curve, 260, 0);
            FfbNode group = small.AddNode(NodeKind.Group, 520, 60);
            FfbNode buffet = small.AddNode(NodeKind.Periodic, 780, 40);
            small.Connect(aoa.Id, "value", shaping.Id, "in");
            small.Connect(shaping.Id, "out", group.Id, "in");
            small.Connect(constant.Id, "value", group.Id, "in");
            small.Connect(group.Id, "out", buffet.Id, "in");
            small.SelectedConnection = small.Graph.Connections[0];
            small.EvaluateLocally(0);
            string smallFile = Path.Combine(outDir, "ffb-graph-editor-small.png");
            Render(new FfbGraphCanvas { Editor = small }, new Size(1100, 360), smallFile);

            // An empty graph shows a hint instead of nothing.
            FfbGraphEditorViewModel empty = new();
            empty.SetGraph(new FfbGraph(), string.Empty);
            string emptyFile = Path.Combine(outDir, "ffb-graph-editor-empty.png");
            Render(new FfbGraphCanvas { Editor = empty }, new Size(600, 300), emptyFile);

            foreach (string file in new[] { canvasFile, curveFile, emptyFile })
                Assert.True(new FileInfo(file).Length > 1000, file);
            using Bitmap bitmap = new(canvasFile);
            Assert.Equal(1500, bitmap.PixelSize.Width);
        });
    }

    // The whole page (needs the application, so it renders only when JOYPRO_FFB_PAGE_PREVIEW is set and then must run
    // alone, like RenderAxisTuningPage):
    //   set JOYPRO_FFB_PAGE_PREVIEW=1 & dotnet test gui/JoyPro.Gui.Tests --filter RenderFfbTelemetryPage
    [Fact]
    public void RenderFfbTelemetryPage()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_FFB_PAGE_PREVIEW")))
            return;
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = AvaloniaRenderThread.Wait(FfbTelemetryTests.CreateLoadedVmAsync());
            vm.SelectedFfbTelemetryGame = vm.FfbTelemetryGames.First(g => g.Id == "DCS");
            vm.FfbTelemetrySetupChanges = "Dry run - would change:\n  Export.lua: add the JoyPro telemetry line";
            RenderPage(vm, Path.Combine(outDir, "ffb-page-settings.png"), 2000);

            // The direction test (fake output): the intro with "Start", then step 1 with "Ready", at 100 % and 150 %.
            vm.FfbTestOutputFactory = _ => new FfbTrimDirectionTests.FakeTestOutput();
            vm.FfbWizardDelay = (_, _) => System.Threading.Tasks.Task.CompletedTask;
            vm.StartFfbDirectionTestCommand.Execute(null);
            AssertWizardButtonsFit(RenderPage(vm, Path.Combine(outDir, "ffb-page-wizard-intro.png"), 2000), "FfbWizardStartButton");
            AvaloniaRenderThread.Wait(Done(vm.FfbWizardReadyCommand.ExecuteAsync(null)));
            AssertWizardButtonsFit(RenderPage(vm, Path.Combine(outDir, "ffb-page-wizard-ready.png"), 2000), "FfbWizardReadyButton");
            AssertWizardButtonsFit(RenderPage(vm, Path.Combine(outDir, "ffb-page-wizard-ready-150.png"), 2000, 144), "FfbWizardReadyButton");
            AvaloniaRenderThread.Wait(Done(vm.FfbWizardReadyCommand.ExecuteAsync(null)));
            AssertWizardButtonsFit(RenderPage(vm, Path.Combine(outDir, "ffb-page-wizard-ask.png"), 2000), null);
            AvaloniaRenderThread.Wait(Done(vm.FfbWizardCloseCommand.ExecuteAsync(null)));

            vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryEditorTab;
            vm.FfbGraphEditor.SelectedNode = vm.FfbGraphEditor.Graph.Nodes.OfType<CurveNode>().First();
            RenderPage(vm, Path.Combine(outDir, "ffb-page-editor.png"));

            FakeFfbHostProcess process = FfbTelemetryTests.StartProcess(vm, launcher, "DCS");
            vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryInspectorTab;
            process.Emit(JoyPro.Ffb.Core.Host.HostProtocol.ValuesLine("telemetry",
                new Dictionary<string, double> { ["ac.ias"] = 154.2, ["ac.aoa"] = 6.5, ["ac.g"] = 2.1, ["dcs.mach"] = 0.48, ["dcs.shells"] = 510 }, 12.3));
            RenderPage(vm, Path.Combine(outDir, "ffb-page-inspector.png"));
            vm.StopFfbTelemetryForShutdown();
        });
    }

    private static async System.Threading.Tasks.Task<bool> Done(System.Threading.Tasks.Task task)
    {
        await task;
        return true;
    }

    // Every visible text in a button of the direction test has its full width plus room (no "Read" for "Ready"); the
    // named button is shown.
    private static void AssertWizardButtonsFit(Control? content, string? expected)
    {
        Assert.NotNull(content);
        List<Button> buttons = content!.GetVisualDescendantsOfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Command is { } command &&
                        (b.Name?.StartsWith("FfbWizard", StringComparison.Ordinal) == true || b.CommandParameter is FfbWizardChoiceItem ||
                         command == ((MainWindowViewModel)content.DataContext!).FfbWizardRetryCommand ||
                         command == ((MainWindowViewModel)content.DataContext!).FfbWizardCancelCommand ||
                         command == ((MainWindowViewModel)content.DataContext!).FfbWizardStartOverCommand ||
                         command == ((MainWindowViewModel)content.DataContext!).FfbWizardCloseCommand))
            .ToList();
        Assert.NotEmpty(buttons);
        if (expected != null)
            Assert.Contains(buttons, b => b.Name == expected);
        foreach (Button button in buttons)
        {
            foreach (TextBlock text in button.GetVisualDescendantsOfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
            {
                Assert.True(text.Bounds.Width + 0.5 >= text.DesiredSize.Width, "'" + text.Text + "' is cut: " + text.Bounds.Width + " < " + text.DesiredSize.Width);
                Assert.True(button.Bounds.Width >= text.DesiredSize.Width, "'" + text.Text + "' is wider than its button.");
            }
        }
    }

    private static Control? RenderPage(MainWindowViewModel vm, string file, double height = 1000, double dpi = 96)
    {
        Size size = new(1500, height);
        Views.MainWindow main = new() { DataContext = vm, Width = size.Width, Height = size.Height };
        main.Measure(size);
        main.Arrange(new Rect(size));
        if (main.Content is not Control content)
            return null;
        for (int pass = 0; pass < 3; pass++)
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            // The window's own layout pass also tells virtualizing lists their viewport (otherwise they show one row).
            main.UpdateLayout();
            AvaloniaRenderThread.Pump();
        }
        // The palette and the inspector show every row (plain StackPanels, no virtualization).
        foreach (ListBox list in content.GetVisualDescendantsOfType<ListBox>().Where(l => l.IsEffectivelyVisible))
            Assert.Equal(list.ItemCount, list.GetVisualDescendantsOfType<ListBoxItem>().Count());
        double scale = dpi / 96;
        using RenderTargetBitmap page = new(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(dpi, dpi));
        using (Avalonia.Media.DrawingContext context = page.CreateDrawingContext())
            context.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F8FAFC")), new Rect(size));
        page.Render(content);
        page.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
        return content;
    }

    private static void Render(Control control, Size size, string file)
    {
        control.Measure(size);
        control.Arrange(new Rect(size));
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(control);
        bitmap.Save(file);
    }
}
