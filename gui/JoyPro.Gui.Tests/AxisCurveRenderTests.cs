using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders the axis curve preview into PNG files without showing a window: %TEMP%\JoyPro2.Gui.Tests\axis-curves, or the
// folder in JOYPRO_AXIS_CURVE_PREVIEW. Only Skia is set up (no Avalonia application), so the other tests are unaffected.
// The Axis Tuning part of the Binds page needs the whole application: that one renders only when
// JOYPRO_AXIS_TUNING_PAGE_PREVIEW is set, and then must run alone (like HudPreviewRenderTests):
//   set JOYPRO_AXIS_TUNING_PAGE_PREVIEW=1 & dotnet test gui/JoyPro.Gui.Tests --filter RenderAxisTuningPage
[SupportedOSPlatform("windows")]
public sealed class AxisCurveRenderTests
{
    public static string OutputFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_AXIS_CURVE_PREVIEW");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "axis-curves")
                : configured;
        }
    }

    [Fact]
    public void RenderAxisCurves()
    {
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        // On the one Avalonia thread of the test run (rendering from the test thread while Avalonia lives on the render
        // thread crashed the test host now and then).
        AvaloniaRenderThread.Run(() =>
        {
            AxisModel dcs = AxisModels.Get(AxisModels.Dcs);
            AxisModel korea = AxisModels.Get(AxisModels.Il2Korea);
            AxisTuning baseTuning = AxisTuning.Parse("inv=0;sl=0;dz=0.05;sx=1;sy=1;cv=0.3");

            // DCS with curvature and dead zone, the stick at half travel.
            string dcsFile = Path.Combine(outDir, "axis-curve-dcs.png");
            Save(new AxisCurveView
            {
                Curve = MainWindowViewModel.SampleAxisCurve(dcs, baseTuning),
                StickInput = 0.5,
                StickOutput = dcs.Evaluate(baseTuning, 0.5)
            }, dcsFile);

            // IL-2 Korea inheriting the base: the fitted curve, the base dashed.
            AxisTuning fitted = AxisModels.Inherit(baseTuning, korea);
            string koreaFile = Path.Combine(outDir, "axis-curve-il2korea-inherited.png");
            Save(new AxisCurveView
            {
                Curve = MainWindowViewModel.SampleAxisCurve(korea, fitted),
                ReferenceCurve = MainWindowViewModel.SampleAxisCurve(dcs, baseTuning),
                StickInput = -0.7,
                StickOutput = korea.Evaluate(fitted, -0.7)
            }, koreaFile);

            // A DCS user curve on a slider.
            AxisTuning userCurve = AxisTuning.Parse("inv=0;sl=1;dz=0;sx=1;sy=1;cv=0|0.02|0.05|0.1|0.2|0.35|0.5|0.65|0.8|0.92|1");
            string curveFile = Path.Combine(outDir, "axis-curve-dcs-usercurve-slider.png");
            Save(new AxisCurveView { Curve = MainWindowViewModel.SampleAxisCurve(dcs, userCurve) }, curveFile);

            // X-Plane free curve points with their draggable handles.
            AxisModel xplane = AxisModels.ForGame("XPlane12");
            AxisTuning stops = new();
            stops.SetControlPoints([new(0, 0), new(0.3, 0.1), new(0.7, 0.55), new(1, 1)]);
            string stopsFile = Path.Combine(outDir, "axis-curve-xplane-handles.png");
            Save(new AxisCurveView
            {
                Curve = MainWindowViewModel.SampleAxisCurve(xplane, stops),
                Handles = stops.ControlPoints().Select(p => new Point(p.Key, xplane.Evaluate(stops, p.Key))).ToList()
            }, stopsFile);

            foreach (string file in new[] { dcsFile, koreaFile, curveFile, stopsFile })
            {
                Assert.True(File.Exists(file), file);
                Assert.True(new FileInfo(file).Length > 1000, file);
            }
            using (Bitmap bitmap = new(dcsFile))
                Assert.Equal(300, bitmap.PixelSize.Width);
        });
    }

    [Fact]
    public void RenderAxisTuningPage()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_AXIS_TUNING_PAGE_PREVIEW")))
            return;
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            // A DCS aircraft with its own user curve (the 11 point sliders).
            RenderPage(Path.Combine(outDir, "axis-tuning-page-dcs-usercurve.png"), "plane:DCS:F-16C_50", vm =>
            {
                AxisParameterEditor curve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
                curve.IsOn = true;
                double[] points = [0, 0.03, 0.08, 0.15, 0.25, 0.37, 0.5, 0.64, 0.78, 0.9, 1];
                for (int i = 0; i < points.Length; i++)
                    curve.Points[i].Value = points[i];
            });
            // IL-2 Korea inheriting the base: fitted to its model, the base dashed.
            RenderPage(Path.Combine(outDir, "axis-tuning-page-il2korea-inherited.png"), "game:IL2Korea", null, fake => fake.Scopes.Remove("game:IL2Korea"));
            // X-Plane with its own free curve points: the table and the handles on the preview.
            RenderPage(Path.Combine(outDir, "axis-tuning-page-xplane-curvepoints.png"), "game:XPlane12", null, fake =>
            {
                fake.IncludeXPlane = true;
                fake.Scopes["game:XPlane12"] = "inv=0;sl=0;dz=0;sx=1;sy=1;cv=0;n.cpN=4;n.cpx0=0;n.cpx1=0.3;n.cpx2=0.7;n.cpx3=1;n.cpy0=0;n.cpy1=0.1;n.cpy2=0.55;n.cpy3=1";
            }, height: 1180);
        });
    }

    // The Binds page with the Axis Tuning expander open on one scope (fake engine), cut to the curve manager.
    private static void RenderPage(string file, string scope, Action<MainWindowViewModel>? edit, Action<AxisTuningFakeEngine>? prepare = null, int height = 760)
    {
        AxisTuningFakeEngine fake = new();
        prepare?.Invoke(fake);
        AxisTuningFakeCapture capture = new();
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake, capture));
        vm.CurrentStage = UiStage.Binds;
        vm.SelectedAxisTuningScope = vm.AxisTuningScopes.Single(s => s.Scope == scope);
        AvaloniaRenderThread.Pump();
        edit?.Invoke(vm);
        vm.OnAxisTuningAxes(new JoystickAxisSnapshot(AxisTuningTests.Stick, [0.5, 0.82, 0.5, 0.5, 0.5, 0.5, 0, 0]));

        Views.MainWindow main = new() { DataContext = vm, Width = 1500, Height = 3200 };
        Size pageSize = new(1500, 3200);
        main.Measure(pageSize);
        main.Arrange(new Rect(pageSize));
        if (main.Content is not Control content)
            return;
        content.Measure(pageSize);
        content.Arrange(new Rect(pageSize));
        AvaloniaRenderThread.Pump();
        content.Measure(pageSize);
        content.Arrange(new Rect(pageSize));

        // Only the part with the curve manager.
        Control? curve = content.GetVisualDescendantsOfType<AxisCurveView>().FirstOrDefault();
        Assert.NotNull(curve);
        Point? top = curve!.TranslatePoint(new Point(0, 0), content);
        Assert.NotNull(top);
        double y = Math.Max(0, top!.Value.Y - 180);
        double x = Math.Max(0, top.Value.X - 20);
        PixelSize size = new((int)Math.Min(1500 - x, 980), (int)Math.Min(3200 - y, height));
        using RenderTargetBitmap page = new(new PixelSize(1500, 3200));
        using (DrawingContext context = page.CreateDrawingContext())
            context.FillRectangle(new SolidColorBrush(Color.Parse("#F8FAFC")), new Rect(pageSize));
        page.Render(content);
        using RenderTargetBitmap cut = new(size);
        using (DrawingContext context = cut.CreateDrawingContext())
            context.DrawImage(page, new Rect(x, y, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
        cut.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    private static void Save(Control control, string file)
    {
        Size size = new(300, 300);
        control.Measure(size);
        control.Arrange(new Rect(size));
        using RenderTargetBitmap bitmap = new(new PixelSize(300, 300));
        bitmap.Render(control);
        bitmap.Save(file);
    }
}

// Just the Skia renderer (no application, styles or windowing): enough for a control that draws itself.
internal static class SkiaOnly
{
    private static readonly object Gate = new();
    private static bool initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized)
                return;
            Avalonia.Skia.SkiaPlatform.Initialize();
            initialized = true;
        }
    }
}

internal static class RenderTestVisuals
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual root) where T : Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}

// One STA thread for every Avalonia render test of this run: Avalonia is set up once per process and ties its UI thread
// to the thread that set it up.
[SupportedOSPlatform("windows")]
internal static class AvaloniaRenderThread
{
    private static readonly System.Collections.Concurrent.BlockingCollection<(Action Action, System.Threading.Tasks.TaskCompletionSource Done)> Work = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        Thread thread = new(() =>
        {
            Exception? setupFailure = null;
            try
            {
                AppBuilder.Configure<App>().UseWin32().UseSkia().SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                setupFailure = ex;
            }
            foreach ((Action action, System.Threading.Tasks.TaskCompletionSource done) in Work.GetConsumingEnumerable())
            {
                if (setupFailure != null)
                {
                    done.SetException(setupFailure);
                    continue;
                }
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }
        })
        { IsBackground = true, Name = "Avalonia render tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    public static void Run(Action action)
    {
        _ = Worker.Value;
        System.Threading.Tasks.TaskCompletionSource done = new();
        Work.Add((action, done));
        done.Task.GetAwaiter().GetResult();
    }

    // Avalonia's synchronisation context sends continuations to this thread: pump it while waiting.
    public static T Wait<T>(System.Threading.Tasks.Task<T> task)
    {
        using CancellationTokenSource done = new();
        task.ContinueWith(_ => done.Cancel(), System.Threading.Tasks.TaskScheduler.Default);
        if (!task.IsCompleted)
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
            }
            catch (PlatformNotSupportedException)
            {
                // A view model test on another thread created the dispatcher before this thread set up the Win32
                // platform (whole-suite runs): that dispatcher has no main loop. Running its jobs until the task ends
                // does the same.
                while (!task.IsCompleted)
                {
                    Pump();
                    Thread.Sleep(5);
                }
            }
        }
        return task.GetAwaiter().GetResult();
    }

    public static void Wait(System.Threading.Tasks.Task task) => Wait(WithResult(task));

    private static async System.Threading.Tasks.Task<bool> WithResult(System.Threading.Tasks.Task task)
    {
        await task;
        return true;
    }

    // A view model test that ran just before (on another thread) can leave Avalonia's dispatcher suspended for a moment;
    // RunJobs then throws "dispatcher processing is suspended". Waiting a little and pumping again lets the render test
    // go on (it failed about every other run when it followed a view model test).
    public static void Pump()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return;
            }
            catch (InvalidOperationException) when (attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
    }
}
