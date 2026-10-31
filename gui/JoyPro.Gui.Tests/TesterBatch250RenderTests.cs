using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The views the 2.5.0 tester batch changed, rendered offscreen at 1280 and 2267 wide, dark and light: the action cards
// (Relations) and the bind cards, the Export page's modes, the Start page's hidden-games line, the engine output with
// its splitter, the Layout page's picture strip and header and the marker assistant in its corner, the HUD designer
// with a grip on its base (stick axes), and the first-export question. Opt-in (needs the whole application, must run
// alone): set JOYPRO_A4_RENDER_OUT=<folder> & dotnet test --filter TesterBatch250RenderTests
[SupportedOSPlatform("windows")]
public sealed class TesterBatch250RenderTests
{
    [Fact]
    public void RenderTesterBatch()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_A4_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "a4-layout");
        Directory.CreateDirectory(work);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync(new UiDesignRenderTests.DesignFakeEngine { HiddenGame = "StarCitizen" }));
            LayoutRedesignRenderTests.LayoutFakeEngine layoutFake = new(LayoutRedesignRenderTests.ResolvePicture(work), work);
            MainWindowViewModel layout = AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.CreateLoadedVmAsync(layoutFake, work));
            MainWindowViewModel hud = AvaloniaRenderThread.Wait(TesterBatch250Tests.HudVmAsync());
            try
            {
                Assert.True(vm.HasHiddenGames);
                // The HUD: the base moved right and forward, the grip twisted; the grip's widget selected.
                hud.OnHudAxes(new JoystickAxisSnapshot(TesterBatch250Tests.Base, [0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
                hud.OnHudAxes(new JoystickAxisSnapshot(TesterBatch250Tests.Base, [0.85, 0.2, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
                hud.OnHudAxes(new JoystickAxisSnapshot(TesterBatch250Tests.Grip, [0, 0, 0, 0.5, 0.5, 0.5, 0, 0]));
                hud.OnHudAxes(new JoystickAxisSnapshot(TesterBatch250Tests.Grip, [0, 0, 0, 0.5, 0.5, 0.75, 0, 0]));
                hud.OnHudInput(new JoystickInputEvent(TesterBatch250Tests.Grip, "JOY_BTN1", true));
                hud.SelectedHudWidget = hud.HudWidgets.Single(w => w.Device?.Device == TesterBatch250Tests.Grip);
                foreach (HudWidget widget in hud.HudWidgets.Where(w => w.IsDevice))
                    widget.Visible = true;

                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    ExportDefaultsRenderTests.RenderDialog(new ConfirmWindow(new ConfirmRequest("First export to DCS World",
                        "Export replaces the binds in DCS World with your profile (a backup is made, Undo export brings them back). Import first?", "Export anyway", false)
                        { SecondaryLabel = "Import first", DontAskKey = MainWindowViewModel.ConfirmFirstExportKey, SecondaryIsPrimary = true }),
                        Path.Combine(outDir, "first-export-question-" + theme + ".png"));
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        int height = width == 1280 ? 800 : 1300;
                        string Name(string page) => Path.Combine(outDir, page + "-" + width + "-" + theme + ".png");

                        vm.EngineOutputExpanded = false;
                        vm.CurrentStage = UiStage.Relations;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relations-cards"));
                        vm.CurrentStage = UiStage.Binds;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("binds-cards"));
                        vm.CurrentStage = UiStage.Export;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, width == 1280 ? 1500 : 1500, Name("export-page"));
                        vm.CurrentStage = UiStage.Start;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, width == 1280 ? 1400 : 1300, Name("start-hidden-games"));
                        vm.EngineOutputExpanded = true;
                        vm.OutputText = string.Join(Environment.NewLine, Enumerable.Range(1, 14).Select(i => "[engine-status] step " + i + " of 14: reading the profile's relations and binds"));
                        AvaloniaRenderThread.Pump();
                        RenderHosted(vm, width, height, Name("engine-output"));
                        vm.EngineOutputExpanded = false;

                        foreach (string page in new[] { "view", "assistant" })
                        {
                            AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.LayoutPages.ShowAsync(layout, page));
                            AvaloniaRenderThread.Pump();
                            UiDesignRenderTests.RenderWindow(layout, width, height, Name("layout-" + page));
                            if (page != "view")
                                continue;
                            // Another picture picked in the strip: kept at once, named above the device list.
                            WorkspacePictureChoice? other = layout.WorkspacePictures.LastOrDefault(p => p.Reference != null && !p.IsSelected);
                            if (other != null)
                            {
                                layout.SelectWorkspacePictureCommand.Execute(other);
                                AvaloniaRenderThread.Wait(layout.DeviceReference.LastPictureChoice);
                                AvaloniaRenderThread.Pump();
                                Assert.StartsWith("Picture: ", layout.LayoutSuggestStatus);
                                Assert.Contains(other.Title, layout.LayoutSuggestStatus);
                                UiDesignRenderTests.RenderWindow(layout, width, height, Name("layout-picture-kept"));
                                layout.SelectWorkspacePictureCommand.Execute(layout.WorkspacePictures.First(p => p.Reference != null));
                                AvaloniaRenderThread.Wait(layout.DeviceReference.LastPictureChoice);
                            }
                        }
                        layout.CloseReferenceAssistantCommand.Execute(null);

                        hud.CurrentStage = UiStage.Hud;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(hud, width, height, Name("hud-designer"));
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                layout.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    // The window's page in a hidden host that runs real layout passes (the engine output's row heights are set by the
    // window's code after the panel opens).
    private static void RenderHosted(MainWindowViewModel vm, int width, int height, string file)
    {
        MainWindow window = new() { DataContext = vm, Width = width, Height = height };
        using HiddenWindow main = HiddenWindow.Create(window, new Size(width, height));
        window.SetEngineOutputHeight(0);
        main.Settle();
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = main.Render();
        bitmap.Save(file);
    }
}
