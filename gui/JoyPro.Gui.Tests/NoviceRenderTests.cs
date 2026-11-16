using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders for the novice review (docs/ui/help-and-tours.md, opt-in: JOYPRO_NOVICE_RENDER_OUT=<folder>): the welcome,
// every step of the pad, quick, new-user and DCS tours, F1's help on the controls a novice clicks, in English and German,
// normal and with easy words; and the quick setup's review for ACE COMBAT 8 + T.Flight HOTAS 4 and IL-2 + URSA MINOR
// (real engine, fake devices, write sandbox) before and after "Use these buttons". Offscreen only. A log next to the
// pictures has every card's text, whether its control was found, and the card and control rectangles.
[SupportedOSPlatform("windows")]
public sealed class NoviceRenderTests
{
    private static string? Out => Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_OUT") is { Length: > 0 } dir ? dir : null;

    [Fact]
    public void Tours_Welcome_Help()
    {
        if (Out is not { } outDir)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder log = new();
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                foreach (string lang in new[] { "en", "de" })
                {
                    if (Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_ONLY") is { Length: > 0 } onlyLang && !onlyLang.StartsWith(lang + "-", StringComparison.Ordinal))
                        continue;
                    vm.ApplyLanguageChoice(lang);
                    foreach (bool easy in new[] { false, true })
                    {
                        string mode = easy ? "easy" : "normal";
                        // JOYPRO_NOVICE_RENDER_ONLY=en-normal (one language and mode per run keeps the memory small).
                        if (Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_ONLY") is { Length: > 0 } only && only != lang + "-" + mode)
                            continue;
                        vm.EasyWords = easy;
                        // JOYPRO_NOVICE_RENDER_PARTS=welcome,help,pad,quick,new,dcs: only these parts (smaller runs, less memory).
                        string[] parts = (Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_PARTS") is { Length: > 0 } p ? p : "welcome,help,pad,quick,new,dcs").Split(',');
                        foreach ((string who, string[] games) in parts.Contains("welcome") ? new[] { ("p1-ac8", new[] { "AceCombat8" }), ("p2-il2", new[] { "IL2Game" }), ("dcs", new[] { "DCS", "IL2Game" }) } : [])
                        {
                            vm.InstalledGamesForWelcome = () => games;
                            // A first open: the suggested answer is picked (not the one picked for the persona before).
                            vm.WelcomeOptions.Clear();
                            vm.OpenWelcome();
                            Shoot(vm, 1280, 800, Path.Combine(outDir, $"welcome-{who}-{mode}-{lang}.png"), _ => { });
                            log.AppendLine($"[{lang} {mode}] WELCOME {who}: {vm.WelcomeSuggestion} | picked = {vm.WelcomeOptions.FirstOrDefault(o => o.IsSelected)?.Id}");
                            foreach (WelcomeOption o in vm.WelcomeOptions)
                                log.AppendLine($"    {o.Id}: {o.Title} / {o.Description}");
                            vm.IsWelcomeOpen = false;
                        }
                        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
                        foreach (string tourId in new[] { TourCatalog.Pad, TourCatalog.Quick, TourCatalog.Newcomer, TourCatalog.Dcs }.Where(parts.Contains))
                        {
                            IReadOnlyList<TourStep> steps = TourCatalog.Find(tourId)!.StepsFor(true);
                            for (int i = 0; i < steps.Count; i++)
                            {
                                vm.StartTour(tourId, everyFeature: true, i);
                                string file = Path.Combine(outDir, $"tour-{tourId}-{i + 1:00}-{mode}-{lang}.png");
                                Control? target = null;
                                Rect card = default, hole = default;
                                string dim = string.Empty;
                                Shoot(vm, 1280, 800, file, layer => target = layer.FindTourTarget(), layer =>
                                {
                                    Border top = layer.FindControl<Border>("DimTop")!;
                                    dim = $"dim visible={top.IsVisible} bounds={top.Bounds} brush={top.Background}";
                                    Border tc = layer.FindControl<Border>("TourCard")!;
                                    card = new Rect(Canvas.GetLeft(tc), Canvas.GetTop(tc), tc.Bounds.Width, tc.Bounds.Height);
                                    if (layer.TourTarget is { } t && t.TranslatePoint(new Point(0, 0), layer) is Point p)
                                        hole = new Rect(p, t.Bounds.Size);
                                });
                                log.AppendLine($"[{lang} {mode}] {tourId} {i + 1}/{steps.Count} target={steps[i].Target} found={target != null} stage={vm.CurrentStage} overlap={hole.Width > 0 && card.Intersects(hole)} card={card} hole={hole} {dim}");
                                log.AppendLine($"    {vm.TourTitle} :: {vm.TourText.Replace("\n", " / ")}" + (vm.TourWaits ? "  {" + vm.TourWaitHint + "}" : string.Empty));
                                vm.EndTourCommand.Execute(null);
                            }
                        }

                        (UiStage Stage, string Page, string Target)[] clicks =
                        [
                            (UiStage.Start, "start", "cmd:QuickSetupCommand"),
                            (UiStage.Start, "start", "cmd:StartImportWizardCommand"),
                            (UiStage.Start, "start", "name:HelpButton"),
                            (UiStage.Start, "start", "nav:binds"),
                            (UiStage.Binds, "binds", "cmd:RecordNextInputCommand"),
                            (UiStage.Export, "export", "cmd:ExportSelectedGamesCommand"),
                            (UiStage.VirtualJoystick, "vjoy", "cmd:StartVirtualJoystickCommand"),
                            (UiStage.VirtualJoystick, "vjoy", "cmd:TestVirtualJoystickCommand")
                        ];
                        int n = 0;
                        foreach ((UiStage stage, string page, string key) in parts.Contains("help") ? clicks : [])
                        {
                            n++;
                            vm.CurrentStage = stage;
                            vm.IsHelpModeOn = true;
                            HelpTopic? topic = null;
                            Shoot(vm, 1280, 800, Path.Combine(outDir, $"help-{n:00}-{Safe(key)}-{mode}-{lang}.png"), layer =>
                            {
                                Control? control = layer.FindTarget(key);
                                topic = layer.ShowHelpFor(control, HelpLayer.PageOf(control, page));
                            });
                            vm.IsHelpModeOn = false;
                            if (topic != null)
                                log.AppendLine($"[{lang} {mode}] HELP {key}: {topic.KindName} | {topic.Title} | LEAD {topic.Lead} | WHAT {topic.What} | NEXT {topic.Next}");
                        }
                    }
                }
            }
            finally
            {
                vm.EasyWords = false;
                vm.ApplyLanguageChoice("en");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.StopGameConfigWatcher();
                File.AppendAllText(Path.Combine(outDir, "tour-log.txt"), log.ToString(), new UTF8Encoding(false));
            }
        });
    }

    // The screens of the second novice review's fixes (opt-in like the rest, JOYPRO_NOVICE_RENDER_OUT; one language and
    // mode per run with JOYPRO_NOVICE_RENDER_ONLY=en-normal): the pad card waiting for the setup window (M1), the helper's
    // install step before and after the license tick and its skip warning (B1), the Test card after the click (M5), when
    // to press Start with the Alt+Tab picture (M6), the quick tour's "It worked!" on the Start page (M4), the list step
    // before Record input (M3), the Start page's one blue button (M8) and the first-start window with Easy words (M2).
    [Fact]
    public void SecondReviewScreens()
    {
        if (Out is not { } outDir)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder log = new();
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                foreach (string lang in new[] { "en", "de" })
                {
                    foreach (bool easy in new[] { false, true })
                    {
                        string mode = easy ? "easy" : "normal";
                        if (Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_ONLY") is { Length: > 0 } only && only != lang + "-" + mode)
                            continue;
                        vm.ApplyLanguageChoice(lang);
                        vm.EasyWords = easy;
                        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
                        List<TourStep> pad = TourCatalog.Find(TourCatalog.Pad)!.StepsFor(false).ToList();
                        List<TourStep> quick = TourCatalog.Find(TourCatalog.Quick)!.StepsFor(false).ToList();
                        ComponentItem? vigem = vm.VirtualJoystickViGEmComponent;
                        bool vigemWas = vm.ViGEmInstalled;
                        (string Name, Action Prepare)[] shots =
                        [
                            ("m1-pad-waits-for-setup", () => { vm.StartTour(TourCatalog.Pad, false, 1); vm.TourTargetClicked(); }),
                            ("b1-install-tick-first", () => { vm.ViGEmInstalled = false; if (vigem != null) vigem.Consent = false; vm.StartTour(TourCatalog.Pad, false, pad.FindIndex(s => s.SkipIf == TourConditions.ViGEmInstalled)); }),
                            ("b1-install-after-tick", () => { if (vigem != null) vigem.Consent = true; }),
                            ("b1-install-skip-warning", () => vm.TourNextCommand.Execute(null)),
                            ("m5-test-after-click", () => { if (vigem != null) vigem.Consent = false; vm.ViGEmInstalled = true; vm.StartTour(TourCatalog.Pad, false, pad.FindIndex(s => s.OpenTab == "Live test")); vm.TourTargetClicked(); }),
                            ("m6-when-to-press-start", () => vm.TourNextCommand.Execute(null)),
                            ("m4-it-worked", () => { vm.ViGEmInstalled = vigemWas; vm.StartQuickTourAfterSetup([vm.GameCards.FirstOrDefault(c => c.Installed)?.GameId ?? "IL2Game"]); }),
                            ("m3-pick-a-row", () => { vm.SelectedBind = null; vm.StartTour(TourCatalog.Quick, false, quick.FindIndex(s => s.WaitFor == TourConditions.BindSelected)); }),
                            ("m3-record-input", () => vm.TourNextCommand.Execute(null)),
                            ("m8-start-page", () =>
                            {
                                vm.EndTourCommand.Execute(null);
                                vm.CurrentStage = UiStage.Start;
                                vm.ApplyAutoSetupStatus([new AutoSetupGameItem("IL2Game", "IL-2 Sturmovik: Great Battles", "empty", 0, 0)]);
                            })
                        ];
                        foreach ((string name, Action prepare) in shots)
                        {
                            prepare();
                            Control? target = null;
                            Shoot(vm, 1280, 800, Path.Combine(outDir, $"r2-{name}-{mode}-{lang}.png"), layer => target = vm.IsTourActive ? layer.FindTourTarget() : null);
                            log.AppendLine($"[{lang} {mode}] {name}: step={vm.TourStepIndex} target={vm.TourTargetKey} found={target?.Name ?? target?.GetType().Name ?? "none"} counter={vm.TourCounter} next={vm.TourNextLabel}");
                            log.AppendLine($"    {vm.TourTitle} :: {vm.TourText.Replace("\n", " / ")}" + (vm.TourShowsWaitHint ? "  {" + vm.TourWaitHint + "}" : string.Empty));
                        }
                        vm.EndTourCommand.Execute(null);

                        // The first-start window with the Easy words box.
                        vm.BuildGameSetup(firstStart: true);
                        GameSetupWindow setup = new() { DataContext = vm, Width = 1040, Height = 780 };
                        RenderWindowContent(setup, new Size(1040, 780), Path.Combine(outDir, $"r2-m2-first-start-{mode}-{lang}.png"));
                        setup.DataContext = null;
                    }
                }
            }
            finally
            {
                vm.EasyWords = false;
                vm.ApplyLanguageChoice("en");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.StopGameConfigWatcher();
                File.AppendAllText(Path.Combine(outDir, "r2-log.txt"), log.ToString(), new UTF8Encoding(false));
            }
        });
    }

    internal static void RenderWindowContent(Window window, Size size, string file)
    {
        Control content = (Control)window.Content!;
        for (int pass = 0; pass < 4; pass++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            content.Measure(size);
            content.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap page = new(new PixelSize((int)size.Width, (int)size.Height));
        page.Render(content);
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#202020")), new Rect(size));
            context.DrawImage(page, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
    }

    // The tour's dim is drawn: a pixel of the page outside the card is darker with the tour than without it.
    [Fact]
    public void TourDim_IsVisible()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.CurrentStage = UiStage.Start;
                double plain = Brightness(vm, 30, 300);
                vm.StartTour(TourCatalog.Newcomer, everyFeature: true, step: 0);
                string dim = string.Empty;
                double dimmed = Brightness(vm, 30, 300, layer =>
                {
                    Border top = layer.FindControl<Border>("DimTop")!;
                    dim = $"visible={top.IsEffectivelyVisible} bounds={top.Bounds} brush={top.Background} opacity={top.Opacity}";
                });
                vm.EndTourCommand.Execute(null);
                Assert.True(dimmed < plain * 0.75, $"page {plain:0.0}, with the tour {dimmed:0.0}; {dim}");
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.StopGameConfigWatcher();
            }
        });
    }

    private static unsafe double Brightness(MainWindowViewModel vm, int x, int y, Action<HelpLayer>? inspect = null)
    {
        string file = Path.Combine(Path.GetTempPath(), "joypro-dim-" + Guid.NewGuid().ToString("N") + ".png");
        double sum = 0;
        Shoot(vm, 1280, 800, file, _ => { }, inspect);
        using (Bitmap bitmap = new(file))
        using (WriteableBitmap copy = new(bitmap.PixelSize, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul))
        {
            using (Avalonia.Platform.ILockedFramebuffer fb = copy.Lock())
            {
                bitmap.CopyPixels(fb, Avalonia.Platform.AlphaFormat.Premul);
                for (int dy = 0; dy < 40; dy++)
                {
                    for (int dx = 0; dx < 200; dx++)
                    {
                        byte* p = (byte*)fb.Address + (y + dy) * fb.RowBytes + (x + dx) * 4;
                        sum += p[0] + p[1] + p[2];
                    }
                }
            }
        }
        File.Delete(file);
        return sum / (40 * 200 * 3);
    }

    // The quick setup's review with the real engine: before and after "Use these buttons", "Change something" opened.
    [Fact]
    public async Task QuickSetupReview()
    {
        if (Out is not { } outDir)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder log = new();
        try
        {
            foreach (string lang in new[] { "en", "de" })
            {
                foreach ((string who, string fixture, string game, string name) in new[]
                         {
                             ("p1-ac8-tflight", "tflight-hotas4.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve"),
                             ("p2-il2-ursa", "ursa-minor.tsv", "IL2Game", "IL-2 Sturmovik: Great Battles")
                         })
                {
                    QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create(fixture, game, name);
                    if (engine.Missing)
                        return;
                    MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, game);
                    vm.ApplyLanguageChoice(lang);
                    await vm.StartQuickSetupAsync();
                    SetupWizardViewModel wizard = vm.SetupWizard!;
                    log.AppendLine($"[{lang}] {who}: review={wizard.IsReviewStep} summary={wizard.QuickSummary}");
                    log.AppendLine($"    APPLY {wizard.ApplyText} / EXPORT {wizard.ExportText}");
                    foreach (SetupReviewRow row in wizard.ReviewRows.Take(14))
                        log.AppendLine($"    ROW {row.Input} -> {row.Name} ({row.Control})");
                    AvaloniaRenderThread.Run(() => Render(wizard, Path.Combine(outDir, $"quick-{who}-1-review-{lang}.png")));
                    wizard.ChangeSomething = true;
                    AvaloniaRenderThread.Run(() => Render(wizard, Path.Combine(outDir, $"quick-{who}-2-change-{lang}.png")));
                    wizard.ChangeSomething = false;
                    await wizard.ApplyCommand.ExecuteAsync(null);
                    log.AppendLine($"    AFTER APPLY: {wizard.Status} / next button: {wizard.ExportText} / apply enabled: {wizard.CanApply}");
                    AvaloniaRenderThread.Run(() => Render(wizard, Path.Combine(outDir, $"quick-{who}-3-applied-{lang}.png")));
                    vm.ApplyLanguageChoice("en");
                    vm.StopGameConfigWatcher();
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "quick-log.txt"), log.ToString(), new UTF8Encoding(false));
        }
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    internal static void Shoot(MainWindowViewModel vm, int w, int h, string file, Action<HelpLayer> prepare, Action<HelpLayer>? after = null)
    {
        // The window lets go of the view model afterwards (else every render stays in memory through the model's events).
        MainWindow main = new() { DataContext = vm };
        using HiddenWindow window = HiddenWindow.Create(main, new Size(w, h));
        try
        {
            HelpLayer layer = window.Page.GetVisualDescendants().OfType<HelpLayer>().Single();
            prepare(layer);
            for (int i = 0; i < 3; i++)
            {
                window.Settle();
                layer.Place();
            }
            window.Settle();
            after?.Invoke(layer);
            using RenderTargetBitmap bitmap = window.Render();
            bitmap.Save(file);
        }
        finally
        {
            window.Page.DataContext = null;
            main.DataContext = null;
        }
    }

    internal static void Render(SetupWizardViewModel vm, string file)
    {
        SetupWizardWindow window = new() { DataContext = vm, Width = 1240, Height = 860 };
        Size size = new(1240, 860);
        Control content = (Control)window.Content!;
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        RenderPanels.UnvirtualizeItemsControls(window);
        for (int pass = 0; pass < 6; pass++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            content.Measure(size);
            content.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap page = new(new PixelSize((int)size.Width, (int)size.Height));
        page.Render(content);
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#202020")), new Rect(size));
            context.DrawImage(page, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
    }
}
