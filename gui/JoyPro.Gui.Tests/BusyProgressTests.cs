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
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The live busy area of engine commands: the stderr parser (engine/EngineProgress.cs format), the busy view model
// (count, bar, step, time estimate, details, cancel), the main view model with a fake engine that streams its lines
// while it runs, and an offscreen render of the panel that checks that no text overlaps another.
[SupportedOSPlatform("windows")]
public sealed class BusyProgressTests
{
    [Fact]
    public void Parser_ReadsStatusProgressAndCancelLines()
    {
        EngineStderrLine status = EngineProgressParser.Parse("[engine-status] dcs refresh id: Discovering headless profile candidates.");
        Assert.Equal(EngineStderrKind.Status, status.Kind);
        Assert.Equal("dcs refresh id: Discovering headless profile candidates.", status.Text);

        EngineStderrLine progress = EngineProgressParser.Parse("[engine-progress] 23/59 DCS World, reading inputs: F-16C_50\r");
        Assert.Equal(EngineStderrKind.Progress, progress.Kind);
        Assert.Equal(23, progress.Current);
        Assert.Equal(59, progress.Total);
        Assert.Equal("DCS World, reading inputs", progress.Phase);
        Assert.Equal("F-16C_50", progress.Item);

        // The item may contain colons and paths; the phase never does.
        EngineStderrLine path = EngineProgressParser.Parse("[engine-progress] 2/3 War Thunder, reading the game's files: C:\\Games\\War Thunder");
        Assert.Equal("War Thunder, reading the game's files", path.Phase);
        Assert.Equal("C:\\Games\\War Thunder", path.Item);

        EngineStderrLine unknownTotal = EngineProgressParser.Parse("[engine-progress] 7/? MSFS 2024, looking for aircraft");
        Assert.Equal(7, unknownTotal.Current);
        Assert.Null(unknownTotal.Total);
        Assert.Equal(string.Empty, unknownTotal.Item);

        Assert.Equal("available", EngineProgressParser.Parse("[engine-cancel] available").CancelState);
        Assert.Equal(EngineStderrKind.Other, EngineProgressParser.Parse("Auto-detected DCS install path: G:\\DCS").Kind);
        Assert.Equal(EngineStderrKind.Other, EngineProgressParser.Parse("[engine-progress] garbage").Kind);
    }

    [Fact]
    public void Parser_StripsTheBusyAreaLinesFromTheResult()
    {
        string stderr = string.Join(Environment.NewLine,
            "[engine-status] Starting command.execute.",
            "[engine-cancel] available",
            "[engine-progress] 1/2 IL-2 Great Battles, reading the key mapping files: global.actions",
            "Warning: something the user should see",
            "[engine-progress] 2/2 IL-2 Great Battles, reading the key mapping files: extra.actions") + Environment.NewLine;

        string quiet = EngineProgressParser.StripProgress(stderr, keepStatus: false);
        Assert.Equal("Warning: something the user should see" + Environment.NewLine, quiet);
        string withStatus = EngineProgressParser.StripProgress(stderr, keepStatus: true);
        Assert.Contains("[engine-status] Starting command.execute.", withStatus);
        Assert.DoesNotContain("[engine-progress]", withStatus);
        Assert.Equal(string.Empty, EngineProgressParser.StripProgress("[engine-progress] 1/1 X, y", keepStatus: false).Trim());

        // The session log keeps the first and last item of every step and about twenty in between.
        string many = string.Join(Environment.NewLine, Enumerable.Range(1, 1000).Select(i => "[engine-progress] " + i + "/1000 DCS World, reading your binds: file" + i));
        string thin = EngineProgressParser.ThinForLog(many + Environment.NewLine + "engine error: boom");
        List<string> kept = thin.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.InRange(kept.Count, 15, 30);
        Assert.Contains(kept, l => l.EndsWith(": file1", StringComparison.Ordinal));
        Assert.Contains(kept, l => l.EndsWith(": file1000", StringComparison.Ordinal));
        Assert.Equal("engine error: boom", kept[^1]);
    }

    [Fact]
    public void BusyProgress_CountsTimesAndEstimates()
    {
        DateTime t0 = new(2026, 10, 11, 20, 0, 0);
        BusyProgressViewModel busy = new();
        busy.Begin("Refreshing the DCS World DB...", t0);
        Assert.True(busy.IsActive);
        Assert.False(busy.IsDeterminate);
        Assert.False(busy.IsLong);
        // A short call never shows the large area (no page jump).
        Assert.False(busy.ShowPanel);

        // Only status lines: indeterminate, the latest message is the step.
        busy.Apply("[engine-status] dcs refresh id: Discovering headless profile candidates.", t0.AddSeconds(1));
        Assert.False(busy.IsDeterminate);
        Assert.Equal("dcs refresh id: Discovering headless profile candidates.", busy.StepText);
        Assert.True(busy.IsLong);

        busy.Apply(["[engine-cancel] available", "[engine-progress] 1/59 DCS World, reading inputs: A-10C"], t0.AddSeconds(10));
        Assert.True(busy.IsDeterminate);
        Assert.Equal("1 / 59", busy.CountText);
        Assert.Equal("A-10C: reading inputs", busy.StepText);
        Assert.Equal("DCS World", busy.GameText);
        Assert.True(busy.CanCancel);
        Assert.Equal(string.Empty, busy.RemainingText);

        for (int i = 2; i <= 23; i++)
            busy.Apply("[engine-progress] " + i + "/59 DCS World, reading inputs: Plane" + i, t0.AddSeconds(10 + (i - 1) * 3));
        busy.Tick(t0.AddSeconds(80));
        Assert.Equal("23 / 59", busy.CountText);
        Assert.Equal(100.0 * 23 / 59, busy.Percent, 3);
        Assert.Equal("1:20", busy.ElapsedText);
        // 3 s per aircraft, 37 to go (the 23rd included), 4 s of the 23rd already gone: 107 s.
        Assert.Equal("about 2 min left", busy.RemainingText);
        Assert.Equal("1:20 elapsed - about 2 min left", busy.TimeText);

        // A new phase starts its own count and estimate.
        busy.Apply("[engine-progress] 1/59 DCS World, writing the command lists: A-10C", t0.AddSeconds(200));
        Assert.Equal("1 / 59", busy.CountText);
        Assert.Equal(string.Empty, busy.RemainingText);

        // Writing: no cancel any more.
        busy.Apply("[engine-cancel] unavailable", t0.AddSeconds(201));
        Assert.False(busy.CanCancel);
        Assert.False(busy.ShowCancel);

        // At most fifty detail lines, the newest first (also from one large batch).
        for (int i = 0; i < 80; i++)
            busy.Apply("[engine-status] line " + i, t0.AddSeconds(210));
        Assert.Equal(BusyProgressViewModel.MaxDetailLines, busy.DetailLines.Count);
        Assert.EndsWith("line 79", busy.DetailLines[0]);
        Assert.EndsWith("line 30", busy.DetailLines[^1]);
        busy.Apply(Enumerable.Range(0, 120).Select(i => "[engine-status] batch " + i).ToList(), t0.AddSeconds(220));
        Assert.Equal(BusyProgressViewModel.MaxDetailLines, busy.DetailLines.Count);
        Assert.EndsWith("batch 119", busy.DetailLines[0]);
        Assert.EndsWith("batch 70", busy.DetailLines[^1]);

        Assert.Equal("about 45 s left", BusyProgressViewModel.FormatRemaining(41));
        Assert.Equal("about 1 h 5 min left", BusyProgressViewModel.FormatRemaining(3900));
        Assert.Equal("1:01:05", BusyProgressViewModel.FormatClock(TimeSpan.FromSeconds(3665)));

        busy.End();
        Assert.False(busy.IsActive);
        Assert.False(busy.ShowCompact);
    }

    [Fact]
    public async Task EngineRun_UpdatesTheBusyAreaWhileTheEngineRuns()
    {
        StreamingFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        Task run = vm.RefreshIl2DbCommand.ExecuteAsync(null);

        await WaitUntil(() => vm.BusyProgress.CountText == "2 / 4", "the busy area should show 2 / 4 while the engine runs");
        Assert.True(vm.BusyProgress.IsActive);
        Assert.True(vm.BusyProgress.ShowPanel);
        Assert.True(vm.IsBusy);
        Assert.True(vm.BusyProgress.IsDeterminate);
        Assert.Equal(50, vm.BusyProgress.Percent, 3);
        Assert.Equal("IL-2 Great Battles", vm.BusyProgress.GameText);
        Assert.Equal("extra.actions: reading the key mapping files", vm.BusyProgress.StepText);
        Assert.Equal("Refreshing the IL-2 DB...", vm.BusyProgress.Title);
        Assert.True(vm.BusyProgress.CanCancel);
        Assert.Contains(vm.BusyProgress.DetailLines, l => l.Contains("Starting command.execute.", StringComparison.Ordinal));
        Assert.False(run.IsCompleted);

        fake.Release.SetResult();
        await run;
        Assert.False(vm.BusyProgress.IsActive);
        Assert.False(vm.IsBusy);
        Assert.Equal("4 / 4", vm.BusyProgress.CountText);

        // The engine ran with the live lines on and a cancel file; the result keeps only the real warning.
        IReadOnlyList<string> call = fake.Calls.Single(c => c.Contains("refresh-il2"));
        Assert.True(AxisTuningTests.Seq(call, "--status", "true"));
        Assert.EndsWith(".request", fake.CancelFiles.Single());
        Assert.Contains("Warning: one action has no description", vm.OutputText);
        Assert.DoesNotContain("[engine-progress]", vm.OutputText);
        Assert.DoesNotContain("[engine-status]", vm.OutputText);
        Assert.False(vm.LastEngineCallCancelled);
    }

    [Fact]
    public async Task Cancel_AsksTheEngineToStopAndReportsIt()
    {
        StreamingFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        Task run = vm.RefreshIl2DbCommand.ExecuteAsync(null);

        await WaitUntil(() => vm.BusyProgress.CanCancel, "the engine offers a cancel while it reads");
        vm.CancelEngineRunCommand.Execute(null);
        Assert.True(vm.BusyProgress.IsCancelling);
        Assert.False(vm.BusyProgress.CanCancel);
        Assert.Equal("Cancelling...", vm.BusyProgress.CancelLabel);

        await run;
        Assert.True(fake.SawCancelFile);
        Assert.True(vm.LastEngineCallCancelled);
        Assert.StartsWith("Cancelled: Refreshing the IL-2 DB stopped before anything was written", vm.StatusText);
        Assert.False(vm.BusyProgress.IsActive);
        // The cancel file is gone after the run.
        Assert.False(File.Exists(fake.CancelFiles.Single()));
    }

    // The panel offscreen at a wide and a narrow width in both themes: every text inside the panel, the step never under
    // the count, the title never under the Cancel button. With JOYPRO_BUSY_PREVIEW the PNGs go there (docs/ui), else to
    // %TEMP%\JoyPro2.Gui.Tests\busy-progress.
    [Fact]
    public void RenderBusyPanel_TextsDoNotOverlap()
    {
        string outDir = Environment.GetEnvironmentVariable("JOYPRO_BUSY_PREVIEW") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "busy-progress");
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            ThemeVariant? before = Application.Current!.RequestedThemeVariant;
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 900, 420 })
                    {
                        BusyProgressViewModel busy = SampleBusy(longItem: width < 500);
                        Border host = new() { Width = width, Child = new BusyProgressPanel { DataContext = busy }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                        host.Classes.Add("callout");
                        // On the page background, in a window (not shown) so the app's styles and resources apply.
                        Panel stage = new() { Background = PageBackground(variant), Children = { host } };
                        Window window = new() { Width = width, Height = 600, Content = stage };
                        Size size = new(width, 600);
                        // Layout only, no dispatcher pumping: in the full test run Dispatcher.UIThread is created by an
                        // earlier view model test before Avalonia is set up (NullDispatcherImpl, every thread counts as
                        // the UI thread), so the hidden window's compositor thread suspends dispatcher processing while
                        // it renders, and RunJobs from here then throws now and then. Measure and arrange are synchronous.
                        for (int pass = 0; pass < 4; pass++)
                        {
                            window.Measure(size);
                            window.Arrange(new Rect(size));
                            stage.Measure(size);
                            stage.Arrange(new Rect(new Size(width, host.DesiredSize.Height)));
                        }
                        SettleProgressBars(stage, new Rect(new Size(width, host.DesiredSize.Height)), pump: false);
                        Rect hostBounds = new(0, 0, width, host.DesiredSize.Height);

                        List<TextBlock> texts = host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
                        TextBlock step = texts.Single(t => t.Text == busy.StepText);
                        TextBlock count = texts.Single(t => t.Text == busy.CountText);
                        TextBlock title = texts.Single(t => t.Text == busy.Title);
                        Button cancel = host.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content is string label && label.StartsWith("Cancel", StringComparison.Ordinal));
                        Assert.Equal("Cancel", cancel.Content);
                        Assert.True(cancel.IsEffectivelyEnabled);
                        Assert.False(BoundsIn(step, host).Intersects(BoundsIn(count, host)), "the step text must not run under the count");
                        Assert.False(BoundsIn(title, host).Intersects(BoundsIn(cancel, host)), "the title must not run under Cancel");
                        foreach (TextBlock text in texts.Where(t => t.FindAncestorOfType<ScrollViewer>() == null))
                        {
                            Rect bounds = BoundsIn(text, host);
                            Assert.True(bounds.Right <= hostBounds.Right + 0.5 && bounds.Bottom <= hostBounds.Bottom + 0.5,
                                "'" + text.Text + "' is outside the busy area (" + bounds + " in " + hostBounds + ")");
                        }
                        ProgressBar bar = host.GetVisualDescendants().OfType<ProgressBar>().Single();
                        Assert.False(bar.IsIndeterminate);
                        Assert.Equal(busy.Percent, bar.Value, 3);
                        // The filled part is as wide as the share of the work done.
                        Control indicator = bar.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Indicator");
                        Assert.InRange(indicator.Bounds.Width / bar.Bounds.Width, busy.Percent / 100 - 0.05, busy.Percent / 100 + 0.05);

                        string file = Path.Combine(outDir, "busy-progress-panel-" + width + "-" + theme + ".png");
                        RenderOnBackground(stage, new PixelSize(width, (int)Math.Ceiling(host.DesiredSize.Height)), variant, file);
                        // The hidden window leaves the render loop: later tests do not race with its compositor.
                        window.Content = null;
                        window.Close();
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = before;
            }
        });
    }

    // The whole window with a running DCS refresh: the Start page's busy area and, on Settings, the compact row above the
    // status line. Needs the whole application, so it renders only when JOYPRO_BUSY_PREVIEW names an output folder:
    //   set JOYPRO_BUSY_PREVIEW=<repo>\docs\ui & dotnet test gui/JoyPro.Gui.Tests --filter RenderBusyWindow
    [Fact]
    public void RenderBusyWindow_StartAreaAndCompactRow()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_BUSY_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            ThemeVariant? before = Application.Current!.RequestedThemeVariant;
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                MainWindowViewModel vm = new(new StreamingFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location
                };
                vm.CurrentStage = UiStage.Start;
                AvaloniaRenderThread.Pump();
                vm.BusyProgress.CancelCommand = vm.CancelEngineRunCommand;
                Fill(vm.BusyProgress);
                vm.BusyProgress.OnStartPage = true;
                vm.StatusText = vm.BusyProgress.Title;
                vm.IsBusy = true;

                MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
                Control page = Layout(main, 1280, 800);
                Border startArea = page.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StartBusyArea");
                Assert.True(startArea.IsEffectivelyVisible);
                Assert.False(page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CompactBusyRow").IsEffectivelyVisible);
                RenderOnBackground(page, new PixelSize(1280, 800), ThemeVariant.Dark, Path.Combine(outDir, "busy-progress-start-1280-dark.png"));

                // Settings: the page's own engine calls (none while busy) must not reset the running command's area.
                vm.CurrentStage = UiStage.Settings;
                AvaloniaRenderThread.Pump();
                if (!vm.BusyProgress.IsActive)
                    Fill(vm.BusyProgress);
                vm.StatusText = vm.BusyProgress.Title;
                Assert.False(vm.BusyProgress.OnStartPage);
                Assert.True(vm.BusyProgress.ShowCompact);
                page = Layout(main, 1280, 800);
                Grid compact = page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CompactBusyRow");
                Assert.True(compact.IsEffectivelyVisible);
                // The bar stays in its column (Fluent's ProgressBar has a minimum width of its own) and shows the share done.
                ProgressBar compactBar = compact.GetVisualDescendants().OfType<ProgressBar>().Single();
                Rect compactBarBounds = BoundsIn(compactBar, compact);
                Assert.True(compactBarBounds.X >= 0 && compactBarBounds.Right <= compact.ColumnDefinitions[0].ActualWidth + 0.5, "the compact bar overflows its column: " + compactBarBounds);
                Control compactFill = compactBar.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Indicator");
                Assert.InRange(compactFill.Bounds.Width / compactBar.Bounds.Width, vm.BusyProgress.Percent / 100 - 0.05, vm.BusyProgress.Percent / 100 + 0.05);
                // The status line (the row below) and the compact row never share pixels.
                TextBlock status = page.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.IsEffectivelyVisible && t.Text == vm.StatusText && t.FindAncestorOfType<Grid>() != compact);
                Assert.False(BoundsIn(compact, page).Intersects(BoundsIn(status, page)), "the compact busy row must not cover the status line");
                RenderOnBackground(page, new PixelSize(1280, 800), ThemeVariant.Dark, Path.Combine(outDir, "busy-progress-settings-1280-dark.png"));
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = before;
            }
        });
    }

    private static Control Layout(MainWindow main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        SettleProgressBars(page, new Rect(size));
        return page;
    }

    // A progress bar sets its fill's width while it is arranged; a shown window's layout manager measures the fill again
    // right after. Offscreen nobody does: one more full pass, every control measured anew.
    private static void SettleProgressBars(Control root, Rect rect, bool pump = true)
    {
        // Twice: a bar that just became visible sets its fill only in the first of these passes.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (Avalonia.Layout.Layoutable layoutable in root.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>())
            {
                layoutable.InvalidateMeasure();
                layoutable.InvalidateArrange();
            }
            root.InvalidateMeasure();
            root.Measure(rect.Size);
            root.Arrange(rect);
            if (pump)
                AvaloniaRenderThread.Pump();
        }
    }

    private static IBrush PageBackground(ThemeVariant variant)
    {
        return Application.Current!.TryFindResource("JP.PageBackgroundBrush", variant, out object? found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(variant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
    }

    private static void RenderOnBackground(Control control, PixelSize pixels, ThemeVariant variant, string file)
    {
        IBrush background = PageBackground(variant);
        Rect area = new(0, 0, pixels.Width, pixels.Height);
        using RenderTargetBitmap content = new(pixels);
        content.Render(control);
        using RenderTargetBitmap bitmap = new(pixels);
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, area);
            context.DrawImage(content, area, area);
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    private static Rect BoundsIn(Visual visual, Visual root)
    {
        Point origin = visual.TranslatePoint(new Point(0, 0), root) ?? new Point(0, 0);
        return new Rect(origin, visual.Bounds.Size);
    }

    // A DCS refresh 80 s in: modules read, 23 of 59 aircraft, Cancel offered, the details open.
    private static BusyProgressViewModel SampleBusy(bool longItem = false)
    {
        BusyProgressViewModel busy = new();
        Fill(busy, longItem);
        return busy;
    }

    private static void Fill(BusyProgressViewModel busy, bool longItem = false)
    {
        DateTime t0 = new(2026, 10, 11, 20, 0, 0);
        busy.CancelCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        busy.Begin("Refreshing the DCS World DB...", t0);
        busy.Apply(["[engine-status] dcs refresh id: DCS refresh id started.", "[engine-cancel] available",
            "[engine-status] dcs refresh id: Discovering headless profile candidates."], t0.AddSeconds(1));
        for (int i = 1; i <= 61; i++)
            busy.Apply("[engine-progress] " + i + "/61 DCS World, reading aircraft modules: Module" + i, t0.AddSeconds(2 + i * 0.1));
        busy.Apply("[engine-status] dcs refresh id: Running headless Lua id refresh.", t0.AddSeconds(9));
        string[] planes = ["A-10A", "A-10C", "A-10C_2", "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "AJS37", "AV8BNA", "Bf-109K-4", "C-101CC", "CH-47Fbl1",
            "Christen Eagle II", "F-14B", "F-15C", "F-15ESE", "F-16C_50", "F-4E-45MC", "F-5E-3", "F-86F Sabre", "F/A-18C_hornet", "FW-190A8", "FW-190D9", "I-16",
            longItem ? "MiG-29 Fulcrum (Flaming Cliffs 2024 edition with the long module name)" : "F-16C_50"];
        for (int i = 1; i <= planes.Length; i++)
            busy.Apply("[engine-progress] " + i + "/59 DCS World, reading inputs: " + planes[i - 1], t0.AddSeconds(10 + (i - 1) * 3));
        busy.DetailsExpanded = true;
        busy.Tick(t0.AddSeconds(80));
    }

    private static MainWindowViewModel CreateVm(StreamingFakeEngine fake)
    {
        return new MainWindowViewModel(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException("Timed out: " + what);
            await Task.Delay(20);
        }
    }

    // An engine that streams the stderr lines of an IL-2 refresh while it runs: status, a cancel offer, two of four
    // files, then waits until the test releases it (the rest, exit 0) or the cancel file appears (exit 3).
    internal sealed class StreamingFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<string> CancelFiles { get; } = [];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawCancelFile { get; private set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, StandardOutput = "{}", Arguments = arguments });
        }

        public async Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
        {
            if (!arguments.Contains("refresh-il2"))
                return await RunAsync(engineExePath, arguments, cancellationToken);
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string cancelFile = options.Environment!["JOYPRO_CANCEL_FILE"];
            CancelFiles.Add(cancelFile);
            List<string> stderr = [];
            async Task Emit(string line)
            {
                stderr.Add(line);
                // Like the real client: on a background thread, while the call is still running.
                await Task.Run(() => options.StandardErrorLine!(line));
            }

            await Emit("[engine-status] Starting command.execute.");
            await Emit("[engine-cancel] available");
            await Emit("[engine-progress] 1/4 IL-2 Great Battles, reading the key mapping files: global.actions");
            await Emit("[engine-progress] 2/4 IL-2 Great Battles, reading the key mapping files: extra.actions");
            await Emit("Warning: one action has no description");
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (!Release.Task.IsCompleted && DateTime.UtcNow < end)
            {
                if (File.Exists(cancelFile))
                {
                    SawCancelFile = true;
                    await Emit("[engine-cancel] cancelled");
                    stderr.Add("engine error: Cancelled by the user while reading the key mapping files. Nothing was written.");
                    return new EngineCommandResult { ExitCode = 3, StandardOutput = string.Empty, StandardError = string.Join(Environment.NewLine, stderr), Arguments = arguments };
                }
                await Task.Delay(20, cancellationToken);
            }
            await Emit("[engine-cancel] unavailable");
            await Emit("[engine-progress] 3/4 IL-2 Great Battles, reading the key mapping files: korea.actions");
            await Emit("[engine-progress] 4/4 IL-2 Great Battles, reading the key mapping files: jp.actions");
            return new EngineCommandResult
            {
                ExitCode = 0,
                StandardOutput = "IL-2 DB Refresh\nNew Actions: 0\n",
                StandardError = string.Join(Environment.NewLine, stderr),
                Arguments = arguments
            };
        }
    }
}
