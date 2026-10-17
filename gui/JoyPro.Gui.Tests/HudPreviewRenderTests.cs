using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders the HUD with the devices connected to this PC into PNG files, without showing a window, to look at the
// design. Runs only when JOYPRO_HUD_PREVIEW names an output folder (and JOYPRO_HUD_ENGINE the engine.exe).
[SupportedOSPlatform("windows")]
public sealed class HudPreviewRenderTests
{
    [Fact]
    public void RenderHudPreview()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_HUD_PREVIEW");
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_HUD_ENGINE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(engine))
            return;
        Directory.CreateDirectory(outDir);
        AppBuilder.Configure<App>().UseWin32().UseSkia().SetupWithoutStarting();

        MainWindowViewModel vm = new(new EngineCliClient(), new NoInputCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
        Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
        Wait(vm.HudPicturesTask);

        // Some input: stick right and forward with twist, throttle levers, a few buttons and a hat.
        foreach (HudWidget widget in vm.HudWidgets.Where(w => w.Device != null))
        {
            HudDevice device = widget.Device!;
            device.ApplyAxes([0.85, 0.2, 0.75, 0.6, 0.35, 0.8, 0.4, 0.3]);
            device.SetInput("JOY_BTN1", true);
            device.SetInput("JOY_BTN2", true);
            device.SetInput("JOY_BTN5", true);
            device.SetInput("JOY_BTN_POV1_UR", true);
        }
        vm.OverlayLines.Add(new OverlayLine("Gear Toggle", "Landing Gear Lever - UP/DOWN", 20, Avalonia.Layout.HorizontalAlignment.Left));
        vm.OverlayLines.Add(new OverlayLine("TMS Up", "TMS - Up (Target Management Switch)", 20, Avalonia.Layout.HorizontalAlignment.Left));
        vm.HudContext.HeaderText = "F-16C Viper · DCS";

        // Arrange as the designer does: measure the widgets on the reference screen, then pack them.
        Grid measureRoot = new() { Width = 1920, Height = 1080 };
        HudSurface measure = new() { Fixed = true, Widgets = vm.HudWidgets, Context = vm.HudContext };
        measureRoot.Children.Add(measure);
        Window measureHost = new() { Content = measureRoot };
        measureHost.Measure(new Size(1920, 1080));
        measureHost.Arrange(new Rect(0, 0, 1920, 1080));
        vm.ArrangeHudWidgets(measure.MeasuredWidgets());
        measureHost.Content = null;

        foreach (string style in new[] { "Glass", "Neon" })
        {
            vm.HudStyle = style;
            Render(vm, Path.Combine(outDir, "hud-" + style.ToLowerInvariant() + ".png"), designer: false);
        }
        vm.HudStyle = "Glass";
        vm.SelectedHudWidget = vm.HudWidgets.FirstOrDefault(w => w.IsDevice && w.Visible);
        Render(vm, Path.Combine(outDir, "hud-designer.png"), designer: true);

        // The HUD page of the main window (not shown).
        vm.CurrentStage = UiStage.Hud;
        Views.MainWindow main = new() { DataContext = vm, Width = 1500, Height = 950 };
        Size pageSize = new(1500, 950);
        main.Measure(pageSize);
        main.Arrange(new Rect(pageSize));
        if (main.Content is Control content)
        {
            content.Measure(pageSize);
            content.Arrange(new Rect(pageSize));
            using RenderTargetBitmap page = new(new PixelSize(1500, 950));
            using (DrawingContext context = page.CreateDrawingContext())
                context.FillRectangle(new SolidColorBrush(Color.Parse("#F8FAFC")), new Rect(pageSize));
            page.Render(content);
            page.Save(Path.Combine(outDir, "hud-page.png"));
        }
        vm.CurrentStage = UiStage.Start;

        // Every device on its own (also the hidden ones), with both pictures where there are two.
        int index = 0;
        foreach (HudWidget widget in vm.HudWidgets.Where(w => w.Device != null))
        {
            foreach (string source in new[] { "Photo", "Layout" })
            {
                if ((source == "Photo" ? widget.Device!.Photo : widget.Device!.LayoutPicture) == null && source == "Layout")
                    continue;
                widget.PictureSource = source;
                RenderWidget(vm, widget, Path.Combine(outDir, "device-" + (++index).ToString("D2") + "-" + source.ToLowerInvariant() + ".png"));
            }
        }
    }

    // Avalonia's synchronisation context sends continuations to this thread: pump it while waiting.
    private static void Wait(System.Threading.Tasks.Task task)
    {
        using System.Threading.CancellationTokenSource done = new();
        task.ContinueWith(_ => done.Cancel(), System.Threading.Tasks.TaskScheduler.Default);
        if (!task.IsCompleted)
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
        task.GetAwaiter().GetResult();
    }

    private static void Render(MainWindowViewModel vm, string file, bool designer)
    {
        Grid root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#1E293B")) };
        root.Children.Add(new HudSurface { Fixed = true, Designer = designer, Widgets = vm.HudWidgets, Context = vm.HudContext });
        Save(root, new Size(1920, 1080), file);
    }

    private static void RenderWidget(MainWindowViewModel vm, HudWidget widget, string file)
    {
        bool visible = widget.Visible;
        double x = widget.X, y = widget.Y;
        widget.Visible = true;
        widget.X = 20;
        widget.Y = 20;
        Grid root = new() { Background = new SolidColorBrush(Color.Parse("#334155")) };
        root.Children.Add(new HudSurface { Widgets = [widget], Context = vm.HudContext });
        Save(root, new Size(1100, 700), file);
        widget.Visible = visible;
        widget.X = x;
        widget.Y = y;
    }

    private static void Save(Control control, Size size, string file)
    {
        // Not shown: the window only gives the control a visual root so styles and templates apply.
        Window host = new() { Content = control, Width = size.Width, Height = size.Height, SizeToContent = SizeToContent.Manual };
        host.Measure(size);
        host.Arrange(new Rect(size));
        control.Measure(size);
        control.Arrange(new Rect(size));
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(control);
        bitmap.Save(file);
    }

    private sealed class NoInputCapture : IJoystickCaptureService
    {
        public string LastDiagnostics => string.Empty;

        public System.Threading.Tasks.Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

        public System.Threading.Tasks.Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
