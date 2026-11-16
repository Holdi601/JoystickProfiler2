using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
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
using Xunit;

namespace JoyPro.Gui.Tests;

// The Start page's auto setup card and the layout window, rendered offscreen and checked (no overlaps, no cut-off text):
// - StartCard_SaysWhatEachGameNeeds (always): the card's texts for games with nothing assigned, with binds of their own
//   and already in the profile;
// - RenderStartCard / RenderLayoutWindow: pictures at several sizes, dark and light, only when JOYPRO_AUTOSETUP_PREVIEW
//   names an output folder (RenderLayoutWindow also needs the engine built; it plans the HOTAS Warthog fixture with the
//   real engine and presses its trigger):
//     set JOYPRO_AUTOSETUP_PREVIEW=%TEMP%\autosetup-ui & dotnet test gui/JoyPro.Gui.Tests --filter AutoSetupRenderTests
[SupportedOSPlatform("windows")]
public sealed class AutoSetupRenderTests
{
    private static List<AutoSetupGameItem> SampleGames() =>
    [
        new("WarThunder", "War Thunder", "empty", 0, 0),
        new("StarCitizen", "Star Citizen", "empty", 0, 0),
        new("MSFS2024", "Microsoft Flight Simulator 2024", "empty", 0, 0),
        new("DCS", "DCS World", "game", 214, 0),
        new("IL2Game", "IL-2 Sturmovik: Great Battles", "profile", 0, 57)
    ];

    private static MainWindowViewModel Vm() =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void StartCard_SaysWhatEachGameNeeds()
    {
        MainWindowViewModel vm = Vm();
        Assert.False(vm.ShowAutoSetupCard);
        vm.ApplyAutoSetupStatus(SampleGames());
        Assert.True(vm.ShowAutoSetupCard);
        Assert.Equal("Set up your devices for Microsoft Flight Simulator 2024, Star Citizen and War Thunder", vm.AutoSetupHeadline);
        Assert.Equal(["DCS"], vm.AutoSetupOwnGames.Select(g => g.GameId));
        Assert.Equal("214 binds in the game", vm.AutoSetupOwnGames[0].Detail);
        Assert.Contains("DCS World", vm.AutoSetupOwnText);
        Assert.Equal(["IL2Game"], vm.AutoSetupProfileGames.Select(g => g.GameId));
        Assert.False(vm.AutoSetupOnlySecondary);

        // Only games with binds: the card stays, as the secondary rows.
        vm.ApplyAutoSetupStatus(SampleGames().Where(g => g.Status != "empty"));
        Assert.True(vm.ShowAutoSetupCard);
        Assert.False(vm.HasAutoSetupNewGames);
        Assert.True(vm.AutoSetupOnlySecondary);
        Assert.Equal("Your games already have binds", vm.AutoSetupHeadline);
    }

    [Fact]
    public void RenderStartCard()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_AUTOSETUP_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            ThemeVariant? before = Application.Current!.RequestedThemeVariant;
            try
            {
                foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int w, int h) in new[] { (1280, 800), (2267, 1250), (1024, 760) })
                    {
                        MainWindowViewModel vm = Vm();
                        vm.CurrentStage = UiStage.Start;
                        vm.ApplyAutoSetupStatus(SampleGames());
                        MainWindow main = new() { DataContext = vm, Width = w, Height = h };
                        Control page = Layout(main, w, h);
                        Border card = page.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AutoSetupCard");
                        Assert.True(card.IsEffectivelyVisible);
                        Check(page, w, h, card);
                        Render(page, new PixelSize(w, h), variant, Path.Combine(outDir, "start-autosetup-" + w + "x" + h + "-" + variant + ".png"));
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = before;
            }
        });
    }

    [Fact]
    public void RenderLayoutWindow()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_AUTOSETUP_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        string engine = FindRepoFile(Path.Combine("engine", "bin", "x64", TestEngine.Configuration, "engine.exe"))
                        ?? throw new InvalidOperationException("Build the engine first.");
        string devices = FindRepoFile(Path.Combine("engine", "tests", "fixtures", "autoassign-matrix", "stick-throttle-pedals.tsv")) ?? throw new InvalidOperationException("Fixture missing.");
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(outDir, "work");
        Directory.CreateDirectory(work);
        string plan = Path.Combine(work, "plan.json");
        RealEngine real = new(engine, devices);
        EngineCommandResult? made = real.RunAsync(["setup", "plan", "--games", "DCS", "--only-aircraft", "F-16C_50,FA-18C_hornet", "--priorities", "F-16C_50",
            "--grip", "044F:0402=thrustmaster-warthog-stick", "--out-plan", plan], false, false, string.Empty, true).GetAwaiter().GetResult();
        Assert.True(made != null && made.ExitCode == 0, made?.StandardError);

        AvaloniaRenderThread.Run(() =>
        {
            ThemeVariant? before = Application.Current!.RequestedThemeVariant;
            try
            {
                foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int w, int h, string device) in new[] { (1280, 900, ""), (1920, 1080, ""), (640, 520, ""), (1920, 1080, "HOTAS Warthog Joystick") })
                    {
                        LayoutWindowSettings settings = new() { Game = "DCS", Aircraft = "F-16C_50", Device = device };
                        LayoutWindowViewModel vm = new(real.RunAsync, plan, id => id == "DCS" ? "DCS World" : id, settings, () => { }, variant == ThemeVariant.Light,
                            post: a => a(), workFolder: Path.Combine(work, variant + "-" + w));
                        AvaloniaRenderThread.Wait(vm.LoadAsync());
                        for (int i = 0; i < 200 && (vm.IsWorking || vm.Devices.Count == 0); i++)
                        {
                            AvaloniaRenderThread.Pump();
                            System.Threading.Thread.Sleep(50);
                        }
                        Assert.True(vm.Devices.Count >= 2, vm.Status);
                        // The trigger is pressed: its row lights up and the function is named at the top.
                        LayoutWindowDevice stick = vm.Devices.First(d => d.Role == "main-stick");
                        stick.Live.FrozenAt = 0;
                        vm.OnInput(new JoystickInputEvent(stick.Device, "JOY_BTN2", true));
                        Assert.True(stick.Live.IsActive("JOY_BTN2"));
                        Assert.True(vm.HasLastFunction);

                        LayoutWindow window = new() { DataContext = vm, Width = w, Height = h };
                        Size size = new(w, h);
                        Control content = (Control)window.Content!;
                        RenderPanels.UnvirtualizeItemsControls(window);
                        for (int pass = 0; pass < 6; pass++)
                        {
                            window.Measure(size);
                            window.Arrange(new Rect(size));
                            content.Measure(size);
                            content.Arrange(new Rect(size));
                            AvaloniaRenderThread.Pump();
                        }
                        Check(content, w, h, null);
                        Render(content, new PixelSize(w, h), variant, Path.Combine(outDir, "layout-window-" + w + "x" + h + (device.Length > 0 ? "-one-device" : string.Empty) + "-" + variant + ".png"));
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = before;
            }
        });
    }

    // ---- helpers ----

    private static void Check(Control root, int w, int h, Control? scope)
    {
        List<string> overlaps = LayoutChecks.Overlaps(root, w, h, scope).Select(o => LayoutChecks.Describe(o.A) + " over " + LayoutChecks.Describe(o.B)).ToList();
        Assert.True(overlaps.Count == 0, w + "x" + h + ": " + string.Join("; ", overlaps));
        List<string> clipped = LayoutChecks.ClippedTexts(root, w, h, scope).Select(c => LayoutChecks.Describe(c.Text) + " needs " + c.Need.ToString("0") + " got " + c.Got.ToString("0")).ToList();
        Assert.True(clipped.Count == 0, w + "x" + h + ": " + string.Join("; ", clipped));
    }

    private static Control Layout(Window main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        RenderPanels.UnvirtualizeItemsControls(main);
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        return page;
    }

    private static void Render(Control control, PixelSize pixels, ThemeVariant variant, string file)
    {
        IBrush background = Application.Current!.TryFindResource("JP.PageBackgroundBrush", variant, out object? found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(variant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
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

    private static string? FindRepoFile(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The engine as the GUI runs it, with the fixture's fake devices; sandboxed writes (TestEnvironment sets the sandbox).
    private sealed class RealEngine(string exe, string devices)
    {
        public async Task<EngineCommandResult?> RunAsync(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string label, bool quiet)
        {
            ProcessStartInfo info = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--status");
            info.ArgumentList.Add("false");
            foreach (string arg in args)
                info.ArgumentList.Add(arg);
            info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
            info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new EngineCommandResult { ExitCode = process.ExitCode, StandardOutput = await output, StandardError = await error, Arguments = args.ToArray() };
        }
    }
}
