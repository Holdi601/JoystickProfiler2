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
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The detail screenshots of the wiki's GUI pages (website/content/gui/*.md): tabs, panels, dialogs and wizard steps in
// the light and the dark theme, with the fake profiles of the design, layout, force feedback, virtual joystick and
// setup wizard renders. Same opt-in as RenderWebsiteScreenshots (JOYPRO_WEBSITE_SHOTS_OUT); JOYPRO_WEBSITE_SHOTS_ONLY
// (comma list of groups: design, settings, hud, layout, ffb, vjoy, wizard, setup) renders only those.
// Every render is searched for texts with this PC's user name or temp folder: they are listed in leaks.txt (a picture
// must show only the neutral demo paths).
public sealed partial class WebsiteScreenshotTests
{
    private static readonly string[] Themes = ["light", "dark"];

    // The profile file of the render's view model (its texts show the demo path instead).
    private static string CurrentRealProfile = string.Empty;

    // The current shot keeps the view model's real profile (Shot(keepProfile: true)).
    private static bool KeepProfileShot;

    [Fact]
    public void RenderWebsiteDetailScreenshots()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_WEBSITE_SHOTS_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        HashSet<string> only = (Environment.GetEnvironmentVariable("JOYPRO_WEBSITE_SHOTS_ONLY") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Wanted(string group) => only.Count == 0 || only.Contains(group);
        List<string> leaks = [];

        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                if (Wanted("design") || Wanted("settings") || Wanted("hud"))
                    DesignShots(outDir, leaks, Wanted("design"), Wanted("settings"), Wanted("hud"));
                if (Wanted("layout"))
                    LayoutShots(outDir, leaks);
                if (Wanted("ffb"))
                    FfbShots(outDir, leaks);
                if (Wanted("vjoy"))
                    VJoyShots(outDir, leaks);
                if (Wanted("wizard"))
                    WizardShots(outDir, leaks);
                if (Wanted("setup"))
                    FirstStartShots(outDir, leaks);
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });

        File.WriteAllLines(Path.Combine(outDir, "leaks.txt"), leaks);
        Assert.True(leaks.Count == 0, "Texts with this PC's paths in the renders:\n" + string.Join("\n", leaks.Take(30)));
    }

    private static void SetTheme(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
        AvaloniaRenderThread.Pump();
    }

    // ---- The design profile (DCS, IL-2 Korea, War Thunder on a Warthog) ----

    private static void DesignShots(string outDir, List<string> leaks, bool design, bool settings, bool hud)
    {
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
        string realProfile = vm.ProfilePath;
        try
        {
            // The Output panel: the Check's engine call, with the demo path in place of the test folder.
            AvaloniaRenderThread.Wait(Done(vm.ValidateProfileCommand.ExecuteAsync(null)));
            string output = vm.OutputText.Replace(realProfile, DemoProfilePath, StringComparison.OrdinalIgnoreCase);
            vm.ShowStatisticsPage = true;
            vm.StatusText = "Ready.";
            AvaloniaRenderThread.Pump();
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                if (design)
                {
                    // Binds: the axis response panel of Pitch (open in the design profile).
                    vm.CurrentStage = UiStage.Binds;
                    vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
                    vm.IsAxisTuningExpanded = true;
                    AvaloniaRenderThread.Pump();
                    Shot(vm, "binds-axis-tuning", theme, 1280, 2300, outDir, leaks,
                        crop: main => main.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Header is string h && h.StartsWith("Axis response", StringComparison.Ordinal)));

                    // Binds: Follow input, a button pressed on the stick selects its relation.
                    vm.FollowInputOnBinds = true;
                    vm.OnLiveInput(new JoystickInputEvent(AxisTuningTests.Stick, "JOY_BTN1", true));
                    vm.OnLiveInput(new JoystickInputEvent(AxisTuningTests.Stick, "JOY_BTN1", false));
                    Settle();
                    Shot(vm, "binds-live", theme, 1280, 800, outDir, leaks);
                    vm.FollowInputOnBinds = false;
                    vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");

                    vm.CurrentStage = UiStage.Modifiers;
                    Shot(vm, "modifiers-tall", theme, 1280, 960, outDir, leaks);
                    vm.CurrentStage = UiStage.VirtualJoystick;
                    Shot(vm, "vjoy-tall", theme, 1280, 1900, outDir, leaks);
                    vm.CurrentStage = UiStage.Statistics;
                    Shot(vm, "statistics-tall", theme, 1280, 1400, outDir, leaks);

                    // Cockpits: the stick page of the selected aircraft (JoyPro's own drawing) and the guide.
                    vm.CurrentStage = UiStage.Cockpits;
                    AvaloniaRenderThread.Pump();
                    if (vm.Cockpits.MainPages.Count > 1)
                        vm.Cockpits.SelectedMainPage = vm.Cockpits.MainPages[1];
                    AvaloniaRenderThread.Pump();
                    Shot(vm, "cockpits-aircraft", theme, 1280, 2150, outDir, leaks);
                    // Down to the end of the stick drawing (the page's notes below it are not part of this picture).
                    CropFile(Path.Combine(outDir, "cockpits-aircraft-" + theme + ".png"), 1522);

                    // The Output panel open under the Export page.
                    vm.CurrentStage = UiStage.Export;
                    vm.OutputText = output;
                    vm.EngineOutputExpanded = true;
                    AvaloniaRenderThread.Pump();
                    Shot(vm, "output", theme, 1280, 900, outDir, leaks,
                        prepare: main => typeof(MainWindow).GetMethod("UpdateEngineOutputCompactLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null));
                    vm.EngineOutputExpanded = false;

                    // A confirmation window over the Export page.
                    ConfirmShot(vm, theme, outDir, leaks);

                    // The game pages: the DCS tile with its aircraft list, the expert options and paths.
                    ExportGameShots(vm, theme, outDir, leaks);
                }
                if (settings)
                    SettingsShots(vm, theme, outDir, leaks);
                if (hud)
                    HudDesignerShots(vm, theme, outDir, leaks);
            }
            if (hud)
                HudSurfaceShots(vm, outDir, leaks);
        }
        finally
        {
            vm.StopHudStreamServer();
            vm.StopGameConfigWatcher();
        }
    }

    private static void SettingsShots(MainWindowViewModel vm, string theme, string outDir, List<string> leaks)
    {
        vm.CurrentStage = UiStage.Settings;
        List<string> order = vm.SettingsSections.Select(s => s.Id).ToList();
        Dictionary<string, bool> open = vm.SettingsSections.ToDictionary(s => s.Id, s => s.IsExpanded);
        try
        {
            foreach (string id in order)
            {
                // The section on top, the only one open; the picture is the section.
                SettingsSectionItem item = vm.FindSettingsSection(id)!;
                vm.SettingsSections.Move(vm.SettingsSections.IndexOf(item), 0);
                foreach (SettingsSectionItem s in vm.SettingsSections)
                    s.IsExpanded = s.Id == id;
                Settle();
                // The games' force feedback settings belong to the open profile: loaded with it and shot without the
                // switch to the demo path (a profile change asks for Reload); the profile's path is shown as the demo's.
                bool ffb = id == "ffb-games";
                if (ffb)
                {
                    AvaloniaRenderThread.Wait(Done(vm.RefreshFfbSettingsCommand.ExecuteAsync(null)));
                    Settle();
                }
                Shot(vm, "settings-" + id, theme, 1280, id == "games" ? 4200 : 2600, outDir, leaks,
                    crop: main => main.GetVisualDescendants().OfType<SettingsSection>().FirstOrDefault(s => s.SectionId == id), keepProfile: ffb);
            }
        }
        finally
        {
            for (int i = 0; i < order.Count; i++)
                vm.SettingsSections.Move(vm.SettingsSections.IndexOf(vm.FindSettingsSection(order[i])!), i);
            foreach (SettingsSectionItem s in vm.SettingsSections)
                s.IsExpanded = open[s.Id];
        }
    }

    private static void ExportGameShots(MainWindowViewModel vm, string theme, string outDir, List<string> leaks)
    {
        vm.CurrentStage = UiStage.Export;
        Settle();
        if (vm.ExportGames.FirstOrDefault(g => g.GameId == "DCS" && g.HasPlanes) is { } dcs)
        {
            dcs.IsExpanded = true;
            Shot(vm, "export-dcs-aircraft", theme, 1280, 2600, outDir, leaks,
                crop: main => main.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => ReferenceEquals(i.ItemsSource, vm.ExportGames)));
            dcs.IsExpanded = false;
        }
        Shot(vm, "export-options", theme, 1280, 3200, outDir, leaks,
            prepare: main => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<Expander>().FirstOrDefault(e => e.Header is string h && h.StartsWith("Options and paths", StringComparison.Ordinal)) is { } x
                ? x.IsExpanded = true
                : false,
            crop: main => main.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Header is string h && h.StartsWith("Options and paths", StringComparison.Ordinal)));
    }

    private static void ConfirmShot(MainWindowViewModel vm, string theme, string outDir, List<string> leaks)
    {
        // The first export of a profile to a game that has binds of its own (MainWindowViewModel.ExportDefaults.cs).
        ConfirmRequest request = new("First export to DCS World",
            "Export replaces the binds in DCS World with your profile (a backup is made, Undo export brings them back). Import first?",
            "Export anyway", IsDanger: false)
        { SecondaryLabel = "Import first", SecondaryIsPrimary = true, DontAskKey = "first-export" };
        ConfirmWindow window = new(request);
        Control content = (Control)window.Content!;
        Size available = new(600, 2000);
        for (int pass = 0; pass < 4; pass++)
        {
            window.Measure(available);
            content.Measure(available);
            AvaloniaRenderThread.Pump();
        }
        Size dialog = new(600, Math.Ceiling(Math.Max(160, content.DesiredSize.Height)));
        for (int pass = 0; pass < 3; pass++)
        {
            content.Measure(dialog);
            content.Arrange(new Rect(dialog));
            AvaloniaRenderThread.Pump();
        }
        IBrush background = window.TryFindResource("JP.SurfaceBrush", window.ActualThemeVariant, out object? res) && res is IBrush own
            ? own
            : new SolidColorBrush(theme == "light" ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        if (content is Panel panel)
            panel.Background = background;
        else if (content is Border border)
            border.Background = background;
        content.Measure(dialog);
        content.Arrange(new Rect(dialog));
        AvaloniaRenderThread.Pump();
        using RenderTargetBitmap dialogContent = new(new PixelSize((int)dialog.Width, (int)dialog.Height));
        dialogContent.Render(content);
        Scan(content, "overlay-confirm", leaks);

        vm.CurrentStage = UiStage.Export;
        string pageFile = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-confirm-page.png");
        Directory.CreateDirectory(Path.GetDirectoryName(pageFile)!);
        Shot(vm, "overlay-confirm-page", theme, 1280, 800, Path.GetDirectoryName(pageFile)!, leaks);
        using Bitmap page = new(Path.Combine(Path.GetDirectoryName(pageFile)!, "overlay-confirm-page-" + theme + ".png"));
        Size size = new(1280, 800);
        Rect at = new((size.Width - dialog.Width) / 2, (size.Height - dialog.Height) / 2, dialog.Width, dialog.Height);
        using RenderTargetBitmap bitmap = new(new PixelSize(1280, 800));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.DrawImage(page, new Rect(size));
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), new Rect(size));
            // The window: its background, a thin border and a shadow-like frame.
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, at.Inflate(new Thickness(22, 20)), 12, 12);
            context.DrawRectangle(background, new Pen(new SolidColorBrush(theme == "light" ? Color.Parse("#C8C8C8") : Color.Parse("#444444")), 1), at.Inflate(new Thickness(16, 14)), 8, 8);
            context.DrawImage(dialogContent, new Rect(dialog), at);
        }
        bitmap.Save(Path.Combine(outDir, "overlay-confirm-" + theme + ".png"));
    }

    // ---- HUD ----

    private static void HudDesignerShots(MainWindowViewModel vm, string theme, string outDir, List<string> leaks)
    {
        vm.CurrentStage = UiStage.Hud;
        AvaloniaRenderThread.Pump();
        HudWidget? selected = vm.SelectedHudWidget;
        if (vm.HudWidgets.FirstOrDefault(w => w.IsAircraft) is { } aircraft)
        {
            vm.SelectedHudWidget = aircraft;
            Shot(vm, "hud-designer-aircraft", theme, 1280, 800, outDir, leaks);
        }
        // A device other than the one the main HUD picture shows selected.
        if (vm.HudWidgets.Where(w => w.IsDevice && w.Visible).Skip(1).FirstOrDefault() is { } device)
        {
            vm.SelectedHudWidget = device;
            Shot(vm, "hud-designer-device", theme, 1280, 800, outDir, leaks);
        }
        vm.SelectedHudWidget = selected;
    }

    private static void HudSurfaceShots(MainWindowViewModel vm, string outDir, List<string> leaks)
    {
        SetTheme("dark");
        vm.HudContext.HeaderText = "F-16C Viper · DCS";
        vm.OverlayLines.Add(new OverlayLine("Gear Toggle", "Landing Gear Lever - UP/DOWN", 20, Avalonia.Layout.HorizontalAlignment.Left));
        vm.OverlayLines.Add(new OverlayLine("TMS Up", "TMS - Up (Target Management Switch)", 20, Avalonia.Layout.HorizontalAlignment.Left));
        HudStreamerRenderTests.Arrange(vm);
        HudStreamerRenderTests.Animate(vm);
        string style = vm.HudStyle;

        // The HUD over a game: a neutral sky and ground in place of a game picture.
        Grid root = new() { Width = 1920, Height = 1080, Background = SkyBrush() };
        root.Children.Add(new HudSurface { Fixed = true, Widgets = vm.HudWidgets, Context = vm.HudContext });
        SaveControl(root, new Size(1920, 1080), Path.Combine(outDir, "hud-overlay-dark.png"));
        File.Copy(Path.Combine(outDir, "hud-overlay-dark.png"), Path.Combine(outDir, "hud-overlay-light.png"), true);

        // A gallery of styles: one device widget in twelve of the styles.
        HudWidget widget = vm.HudWidgets.First(w => w.IsDevice && w.Visible);
        string[] styles = HudStreamerRenderTests.Presets.Take(11).Append("Vector wireframe").ToArray();
        const int cellW = 520, cellH = 360, cols = 4;
        int rows = (styles.Length + cols - 1) / cols;
        using RenderTargetBitmap gallery = new(new PixelSize(cellW * cols, cellH * rows));
        double x0 = widget.X, y0 = widget.Y, s0 = widget.Scale;
        try
        {
            using DrawingContext context = gallery.CreateDrawingContext();
            context.FillRectangle(new SolidColorBrush(Color.Parse("#334155")), new Rect(0, 0, cellW * cols, cellH * rows));
            for (int i = 0; i < styles.Length; i++)
            {
                vm.HudStyle = styles[i];
                widget.X = 14;
                widget.Y = 10;
                widget.Scale = 1;
                AvaloniaRenderThread.Pump();
                // The style's name in a strip above the widget.
                Grid cell = new() { Background = SkyBrush(), RowDefinitions = new RowDefinitions("30,*") };
                cell.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#0F172A")),
                    Child = new TextBlock
                    {
                        Text = styles[i], Margin = new Thickness(12, 0, 0, 0), FontSize = 15, FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                });
                HudSurface surface = new() { Widgets = [widget], Context = vm.HudContext };
                Grid.SetRow(surface, 1);
                cell.Children.Add(surface);
                using RenderTargetBitmap one = RenderControl(cell, new Size(cellW - 8, cellH - 8));
                context.DrawImage(one, new Rect(0, 0, cellW - 8, cellH - 8), new Rect(i % cols * cellW + 4, i / cols * cellH + 4, cellW - 8, cellH - 8));
            }
        }
        finally
        {
            widget.X = x0;
            widget.Y = y0;
            widget.Scale = s0;
            vm.HudStyle = style;
        }
        gallery.Save(Path.Combine(outDir, "hud-styles-dark.png"));
        File.Copy(Path.Combine(outDir, "hud-styles-dark.png"), Path.Combine(outDir, "hud-styles-light.png"), true);

        StreamPageShot(vm, outDir);
    }

    private static IBrush SkyBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#4A6FA5"), 0),
            new GradientStop(Color.Parse("#9DB8D9"), 0.55),
            new GradientStop(Color.Parse("#6B7B5A"), 0.56),
            new GradientStop(Color.Parse("#3E4A33"), 1)
        }
    };

    // The page for OBS (http://127.0.0.1:<port>/hud) in a headless Edge, as OBS's browser source shows it over the game.
    private static void StreamPageShot(MainWindowViewModel vm, string outDir)
    {
        string? edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        }.FirstOrDefault(File.Exists);
        if (edge == null)
            return;
        vm.HudStreamPort = HudStreamerTests.FreePort();
        vm.StartHudStreamServer();
        string shot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-stream.png");
        string profile = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "edge-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(shot))
                File.Delete(shot);
            ProcessStartInfo start = new(edge) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--hide-scrollbars", "--user-data-dir=" + profile,
                         "--window-size=1920,1080", "--default-background-color=00000000", "--timeout=6000", "--screenshot=" + shot, vm.HudStreamUrl })
                start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            Stopwatch clock = Stopwatch.StartNew();
            while (!process.HasExited && clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                AvaloniaRenderThread.Pump();
                vm.HudPump.Flush();
                System.Threading.Thread.Sleep(30);
            }
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        finally
        {
            vm.StopHudStreamServer();
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception)
            {
            }
        }
        if (!File.Exists(shot))
            return;
        // The transparent page over the same neutral sky as the overlay picture (OBS puts it over the game capture).
        using Bitmap page = new(shot);
        Size size = new(page.PixelSize.Width, page.PixelSize.Height);
        Grid root = new() { Width = size.Width, Height = size.Height, Background = SkyBrush() };
        root.Children.Add(new Image { Source = page, Stretch = Stretch.None });
        SaveControl(root, size, Path.Combine(outDir, "hud-stream-dark.png"));
        File.Copy(Path.Combine(outDir, "hud-stream-dark.png"), Path.Combine(outDir, "hud-stream-light.png"), true);
    }

    // ---- Layout (the layout renders' profile: JoyPro's drawing of the Warthog stick, a button box) ----

    private static void LayoutShots(string outDir, List<string> leaks)
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-layout");
        Directory.CreateDirectory(work);
        string picture = LayoutRedesignRenderTests.ResolvePicture(work);
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(picture, work);
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.CreateLoadedVmAsync(fake, work));
        try
        {
            (string Page, string Name)[] pages =
            [
                ("view", "layout-picture"), ("bind", "layout-bind"), ("tables", "layout-tables"), ("assistant", "layout-markers"),
                ("cheatsheets", "layout-cheat-sheets"), ("visualbind", "binds-visual-assign")
            ];
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                foreach ((string page, string name) in pages)
                {
                    AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.LayoutPages.ShowAsync(vm, page));
                    if (page == "cheatsheets")
                        vm.LayoutExportPath = @"C:\Users\Pilot\Documents\JoyPro\Cheat sheets";
                    if (page == "cheatsheets")
                        vm.CheatSheetStatus = vm.CheatSheetStatus.Replace(Path.Combine(work, "Cheat sheets"), vm.LayoutExportPath, StringComparison.OrdinalIgnoreCase);
                    vm.StatusText = "Ready.";
                    Settle();
                    Shot(vm, name, theme, 1280, page is "cheatsheets" or "visualbind" ? 800 : 1300, outDir, leaks);
                    // The Layout page's main picture (the page as it opens): the devices and a picture with its markers.
                    if (page == "view")
                        Shot(vm, "layout", theme, 1280, 1100, outDir, leaks);
                    if (page == "cheatsheets")
                        vm.LayoutExportPath = Path.Combine(work, "Cheat sheets");
                }
            }
        }
        finally
        {
            vm.CloseReferenceAssistantCommand.Execute(null);
            vm.StopGameConfigWatcher();
        }
    }

    // ---- Force feedback: the graph editor and the telemetry inspector ----

    private static void FfbShots(string outDir, List<string> leaks)
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = AvaloniaRenderThread.Wait(FfbTelemetryTests.CreateLoadedVmAsync());
        vm.SelectedFfbTelemetryGame = vm.FfbTelemetryGames.First(g => g.Id == "DCS");
        FakeFfbHostProcess process = FfbTelemetryTests.StartProcess(vm, launcher, "DCS");
        process.Emit(JoyPro.Ffb.Core.Host.HostProtocol.ValuesLine("telemetry",
            new Dictionary<string, double> { ["ac.ias"] = 154.2, ["ac.aoa"] = 6.5, ["ac.g"] = 2.1, ["dcs.mach"] = 0.48, ["dcs.shells"] = 510 }, 12.3));
        try
        {
            vm.StatusText = "Ready.";
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryEditorTab;
                vm.FfbGraphEditor.SelectedNode = vm.FfbGraphEditor.Graph.Nodes.OfType<JoyPro.Ffb.Core.Graph.CurveNode>().FirstOrDefault();
                AvaloniaRenderThread.Pump();
                Shot(vm, "ffb-editor", theme, 1280, 900, outDir, leaks);
                vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryInspectorTab;
                AvaloniaRenderThread.Pump();
                Shot(vm, "ffb-inspector", theme, 1280, 900, outDir, leaks);
                vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetrySettingsTab;
                AvaloniaRenderThread.Pump();
                Shot(vm, "ffb-tall", theme, 1280, 2000, outDir, leaks);
            }
        }
        finally
        {
            vm.StopFfbTelemetryForShutdown();
        }
    }

    // ---- Virtual joystick: the functions graph with the editor of a chained function ----

    private static void VJoyShots(string outDir, List<string> leaks)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-vjoy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        VfFakeEngine engine = VirtualFunctionGraphModesGuiTests.EngineWithChain();
        engine.Modes.Modes.Add(new VfMode { Name = "Default" });
        engine.Modes.Modes.Add(new VfMode { Name = "Combat", Parent = "Default" });
        engine.Modes.Modes.Add(new VfMode { Name = "Landing", Parent = "Default" });
        engine.Modes.Start = "Combat";
        VirtualFunction toggle = engine.FunctionsOf("DCS").Single(f => f.Id == "f2");
        toggle.InputOf("in")!.Device = "fn:f1";
        toggle.InputOf("in")!.Input = "BAND2";
        toggle.Modes = ["Combat"];
        VirtualFunction pinky = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.ModeSwitch, "Landing while held");
        pinky.Id = "f4";
        pinky.Params["action"] = VirtualFunctionCatalog.ModeActionHold;
        pinky.SwitchModes = ["Landing"];
        pinky.InputOf("in")!.Device = VirtualFunctionsGuiTests.Stick;
        pinky.InputOf("in")!.Input = "JOY_BTN5";
        engine.FunctionsOf("DCS").Add(pinky);
        (MainWindowViewModel vm, _, _) = AvaloniaRenderThread.Wait(VirtualFunctionsGuiTests.CreateVmAsync(folder, engine));
        vm.ApplyVirtualModeLive("mode DCS Combat");
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions.Single(f => f.Id == "f2"));
        vm.StatusText = "Ready.";
        vm.CurrentStage = UiStage.VirtualJoystick;
        foreach (string theme in Themes)
        {
            SetTheme(theme);
            Shot(vm, "vjoy-functions", theme, 1280, 1300, outDir, leaks,
                prepare: main => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<VirtualJoystickView>().FirstOrDefault() is { } view &&
                                 Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<TabControl>().FirstOrDefault() is { } tabs
                    ? tabs.SelectedIndex = 2
                    : 0);
        }
    }

    // ---- The setup wizard (its fake engine), every step ----

    private static void WizardShots(string outDir, List<string> leaks)
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-wizard-" + Guid.NewGuid().ToString("N"));
        SetupWizardRenderTests.FakeSetupEngine fake = new();
        SetupWizardViewModel vm = new(fake.RunAsync, [new SetupGameChoice("DCS", "DCS World", true), new SetupGameChoice("StarCitizen", "Star Citizen", true),
            new SetupGameChoice("WarThunder", "War Thunder", false), new SetupGameChoice("IL2Game", "IL-2 Sturmovik: Great Battles", false)], workFolder: work);
        void Both(string name)
        {
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                DialogShot(new SetupWizardWindow { DataContext = vm }, new Size(1400, 900), Path.Combine(outDir, name + "-" + theme + ".png"), name, leaks);
            }
        }
        Both("wizard-games");
        AvaloniaRenderThread.Wait(Done(vm.NextCommand.ExecuteAsync(null)));
        Both("wizard-devices");
        AvaloniaRenderThread.Wait(Done(vm.NextCommand.ExecuteAsync(null)));
        vm.AddPriority("F-16C_50");
        vm.AddPriority("FA-18C_hornet");
        vm.AddPriority("A-10C II", equalToLast: true);
        vm.AddPriority("StarCitizen");
        vm.Modifiers = 1;
        vm.Realism = 30;
        vm.AircraftFilter = "F-";
        Both("wizard-priorities");
        AvaloniaRenderThread.Wait(Done(vm.NextCommand.ExecuteAsync(null)));
        Both("wizard-plan");
        AvaloniaRenderThread.Wait(Done(vm.NextCommand.ExecuteAsync(null)));
        vm.SelectedReviewDevice = vm.ReviewDevices.FirstOrDefault(d => d.Id.StartsWith("VKB", StringComparison.Ordinal)) ?? vm.SelectedReviewDevice;
        AvaloniaRenderThread.Wait(Done(vm.RefreshSheetAsync()));
        vm.SelectedRow = vm.ReviewRows.FirstOrDefault(r => !r.IsKept && !r.Relation.IsAxis);
        Both("wizard-review");
    }

    // ---- The first-start setup ("Your games") ----

    private static void FirstStartShots(string outDir, List<string> leaks)
    {
        GameSetupRenderTests.SetupFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new GameSetupRenderTests.NoCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        AvaloniaRenderThread.Wait(Done(vm.RefreshGameInstallsCommand.ExecuteAsync(null)));
        vm.BuildGameSetup(firstStart: true);
        vm.GameSetupItems.Single(i => i.GameId == "DCS").ConfigLink = @"C:\Users\Pilot\Saved Games\DCS.openbeta";
        vm.GameSetupItems.Single(i => i.GameId == "IL2Game").HasGame = true;
        vm.GameSetupItems.Single(i => i.GameId == "MSFS2020").HasGame = false;
        foreach (string theme in Themes)
        {
            SetTheme(theme);
            DialogShot(new GameSetupWindow { DataContext = vm }, new Size(1040, 1300), Path.Combine(outDir, "first-start-" + theme + ".png"), "first-start", leaks);
        }
    }

    // ---- Rendering ----

    // The main window at width x height (like UiDesignRenderTests.RenderWindow), optionally cut to one control.
    private static void Shot(MainWindowViewModel vm, string name, string theme, int width, int height, string outDir, List<string> leaks,
        Func<MainWindow, object?>? prepare = null, Func<MainWindow, Control?>? crop = null, int margin = 12, bool keepProfile = false)
    {
        Settle();
        string realProfile = vm.ProfilePath;
        CurrentRealProfile = realProfile;
        if (!keepProfile)
            vm.ProfilePath = DemoProfilePath;
        KeepProfileShot = keepProfile;
        try
        {
            ShotCore(vm, name, theme, width, height, outDir, leaks, prepare, crop, margin);
        }
        finally
        {
            KeepProfileShot = false;
            if (vm.ProfilePath != realProfile)
                vm.ProfilePath = realProfile;
        }
    }

    // Pending engine calls and posted updates finish (they need the profile's real folder; the demo path is only in the picture).
    private static void Settle()
    {
        AvaloniaRenderThread.Wait(Task.Delay(250));
        AvaloniaRenderThread.Pump();
    }

    private static void ShotCore(MainWindowViewModel vm, string name, string theme, int width, int height, string outDir, List<string> leaks,
        Func<MainWindow, object?>? prepare, Func<MainWindow, Control?>? crop, int margin)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        void PlainLists()
        {
            // Offscreen, the virtualizing lists do not learn their viewport: they show all their rows in a plain panel.
            foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
            {
                list.AutoScrollToSelectedItem = false;
                if (!ReferenceEquals(list.ItemsPanel, PlainPanel))
                    list.ItemsPanel = PlainPanel;
            }
            RenderPanels.UnvirtualizeItemsControls(main);
        }
        // Before the first layout (a tab chosen later would fade in, and offscreen no animation runs).
        prepare?.Invoke(main);
        PlainLists();
        for (int pass = 0; pass < 7; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            main.UpdateLayout();
            AvaloniaRenderThread.Pump();
            if (pass == 1)
                PlainLists();
        }
        if (Neutralize(page))
        {
            for (int pass = 0; pass < 2; pass++)
            {
                page.Measure(size);
                page.Arrange(new Rect(size));
                main.UpdateLayout();
                AvaloniaRenderThread.Pump();
            }
        }
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? res) && res is IBrush own
            ? own
            : new SolidColorBrush(main.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        Rect area = new(size);
        if (crop?.Invoke(main) is { } target && target.TranslatePoint(new Point(0, 0), page) is { } at)
            area = new Rect(at, target.Bounds.Size).Inflate(margin).Intersect(new Rect(size));
        area = new Rect(Math.Floor(area.X), Math.Floor(area.Y), Math.Floor(area.Width), Math.Floor(area.Height));
        using RenderTargetBitmap bitmap = new(new PixelSize((int)area.Width, (int)area.Height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(area.Size));
            context.DrawImage(content, area, new Rect(area.Size));
        }
        string file = Path.Combine(outDir, name + "-" + theme + ".png");
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
        Scan(page, name, leaks);
    }

    // A plain StackPanel as the items panel of a list (no virtualization offscreen).
    private static readonly Avalonia.Controls.Templates.FuncTemplate<Panel?> PlainPanel = new(() => new StackPanel());

    // A window of its own (the setup wizard, the first-start setup) on the theme's page background.
    private static void DialogShot(Window window, Size size, string file, string name, List<string> leaks)
    {
        window.Width = size.Width;
        window.Height = size.Height;
        Control content = (Control)window.Content!;
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = PlainPanel;
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
        Neutralize(content);
        IBrush background = window.TryFindResource("JP.PageBackgroundBrush", window.ActualThemeVariant, out object? res) && res is IBrush own
            ? own
            : new SolidColorBrush(window.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        switch (content)
        {
            case Panel panel:
                panel.Background = background;
                break;
            case Border border:
                border.Background = background;
                break;
            case Avalonia.Controls.Primitives.TemplatedControl templated:
                templated.Background = background;
                break;
        }
        content.Measure(size);
        content.Arrange(new Rect(size));
        AvaloniaRenderThread.Pump();
        using RenderTargetBitmap page = new(new PixelSize((int)size.Width, (int)size.Height));
        page.Render(content);
        const int pad = 16;
        Size framed = new(size.Width + 2 * pad, size.Height + 2 * pad);
        using RenderTargetBitmap bitmap = new(new PixelSize((int)framed.Width, (int)framed.Height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(framed));
            context.DrawImage(page, new Rect(size), new Rect(pad, pad, size.Width, size.Height));
        }
        bitmap.Save(file);
        Scan(content, name, leaks);
    }

    private static void CropFile(string file, int height)
    {
        PixelSize size;
        using (Bitmap source = new(file))
        {
            size = new PixelSize(source.PixelSize.Width, Math.Min(height, source.PixelSize.Height));
            using RenderTargetBitmap cropped = new(size);
            using (DrawingContext context = cropped.CreateDrawingContext())
                context.DrawImage(source, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
            string temp = file + ".tmp.png";
            cropped.Save(temp);
            source.Dispose();
            File.Move(temp, file, true);
        }
    }

    private static RenderTargetBitmap RenderControl(Control control, Size size)
    {
        Window host = new() { Content = control, Width = size.Width, Height = size.Height, SizeToContent = SizeToContent.Manual };
        for (int pass = 0; pass < 3; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            control.Measure(size);
            control.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(control);
        host.Content = null;
        return bitmap;
    }

    private static void SaveControl(Control control, Size size, string file)
    {
        using RenderTargetBitmap bitmap = RenderControl(control, size);
        bitmap.Save(file);
    }

    // Texts that would show this PC in a public picture: the user name, the temp folder, the test folders.
    // The test's data and temp folders shown as text (log folder, data folder) read as the demo user's folders.
    private static bool Neutralize(Control root)
    {
        string? data = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")?.TrimEnd('\\');
        string temp = Path.GetTempPath().TrimEnd('\\');
        bool changed = false;
        foreach (Visual visual in root.GetVisualDescendants().ToList())
        {
            string? text = visual switch { TextBlock t => t.Text, TextBox b => b.Text, _ => null };
            if (string.IsNullOrEmpty(text))
                continue;
            string fixedText = text;
            if (CurrentRealProfile.Length > 0)
                fixedText = fixedText.Replace(CurrentRealProfile, DemoProfilePath, StringComparison.OrdinalIgnoreCase);
            // The test sandbox's user folders (Saved Games, Documents) are the demo user's.
            fixedText = System.Text.RegularExpressions.Regex.Replace(fixedText,
                System.Text.RegularExpressions.Regex.Escape(temp) + @"\\JoyPro2\.Gui\.Tests\\sandbox-[0-9a-f]+\\UserFolders\\UserProfile", @"C:\Users\Pilot",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // ... and its program folders (a game found there, Star Citizen's profile folder) the demo PC's.
            fixedText = System.Text.RegularExpressions.Regex.Replace(fixedText,
                System.Text.RegularExpressions.Regex.Escape(temp) + @"\\JoyPro2\.Gui\.Tests\\sandbox-[0-9a-f]+\\UserFolders\\ProgramFiles(X86)?",
                m => m.Groups[1].Success ? @"C:\Program Files (x86)" : @"C:\Program Files", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(data))
                fixedText = fixedText.Replace(data, @"C:\Users\Pilot\AppData\Local\JoyPro2", StringComparison.OrdinalIgnoreCase);
            fixedText = fixedText.Replace(temp, @"C:\Users\Pilot\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase);
            if (fixedText == text)
                continue;
            if (visual is TextBlock block)
                block.Text = fixedText;
            else if (visual is TextBox box)
            {
                // A shot that keeps the real profile hides the profile path box: its text would flow back into the view
                // model and switch the open profile (the force feedback settings then ask for Reload).
                if (KeepProfileShot && CurrentRealProfile.Length > 0 && text.Contains(CurrentRealProfile, StringComparison.OrdinalIgnoreCase))
                    box.IsVisible = false;
                else
                    box.Text = fixedText;
            }
            changed = true;
        }
        return changed;
    }

    private static void Scan(Control root, string name, List<string> leaks)
    {
        string user = Environment.UserName;
        string temp = Path.GetTempPath().TrimEnd('\\');
        foreach (Visual visual in root.GetVisualDescendants())
        {
            string? text = visual switch
            {
                TextBlock t => t.Text,
                TextBox b => b.Text,
                _ => null
            };
            if (string.IsNullOrEmpty(text) || !visual.IsEffectivelyVisible)
                continue;
            if ((user.Length >= 3 && text.Contains(user, StringComparison.OrdinalIgnoreCase)) || text.Contains(temp, StringComparison.OrdinalIgnoreCase) ||
                text.Contains("JoyPro2.Gui.Tests", StringComparison.OrdinalIgnoreCase))
                leaks.Add(name + ": " + text.Replace("\n", " | ")[..Math.Min(text.Length, 240)]);
        }
    }
}
