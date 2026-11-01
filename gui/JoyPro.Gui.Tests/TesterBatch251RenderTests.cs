using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The views the 2.5.1 batch changed, rendered offscreen at 1280 and 2267 wide, dark and light: the Start page and
// Settings > Devices with a device of the profile that is not connected, the Relations page with its filter block open
// and folded up (with active filters), the Check's cards with "More" and "Open a relation", the telemetry force feedback
// page and Settings > Telemetry recording with War Thunder hidden. Opt-in (needs the whole application, must run alone):
// set JOYPRO_251_RENDER_OUT=<folder> & dotnet test --filter TesterBatch251RenderTests
[SupportedOSPlatform("windows")]
public sealed class TesterBatch251RenderTests
{
    private const string Offline = "Saitek Pro Flight X-56 Rhino Stick {00000000-0000-0000-0000-0000000000d7}";

    [Fact]
    public void RenderTesterBatch251()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_251_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync(new UiDesignRenderTests.DesignFakeEngine { HiddenGame = "WarThunder", OfflineDevice = Offline }));
            MainWindowViewModel check = new(new DcsDeviceCopiesTests.CopiesFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "tester-batch-251-render.pr0file")
            };
            AvaloniaRenderThread.Wait(check.ValidateProfileCommand.ExecuteAsync(null));
            // The profile's devices (one of them not connected) and the cards again.
            AvaloniaRenderThread.Wait(vm.RefreshDevicesCommand.ExecuteAsync(null));
            AvaloniaRenderThread.Wait(vm.LoadDeviceCardsCommand.ExecuteAsync(null));
            try
            {
                Assert.True(vm.DeviceCards.Any(c => c.NotConnected), "Profile devices: " + string.Join(", ", vm.ProfileDevices));
                Assert.DoesNotContain(vm.VisibleFfbTelemetryGames, g => g.Id == "WarThunder");
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        int height = width == 1280 ? 800 : 1300;
                        string Name(string page) => Path.Combine(outDir, page + "-" + width + "-" + theme + ".png");
                        vm.EngineOutputExpanded = false;

                        vm.CurrentStage = UiStage.Start;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, width == 1280 ? 1400 : 1300, Name("start"));

                        ShowSettings(vm, "devices");
                        UiDesignRenderTests.RenderWindow(vm, width, 2800, Name("settings-devices"));
                        ShowSettings(vm, "telemetry-recording");
                        UiDesignRenderTests.RenderWindow(vm, width, 2400, Name("settings-telemetry-recording"));

                        vm.CurrentStage = UiStage.Relations;
                        vm.IsRelationFiltersOpen = true;
                        vm.RelationFilter.Bound = "all";
                        vm.RelationFilter.Kind = "all";
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relations-filters-open"));
                        vm.RelationFilter.Bound = "bound";
                        vm.RelationFilter.Kind = "axis";
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relations-filters-open-active"));
                        vm.IsRelationFiltersOpen = false;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relations-filters-closed-active"));
                        vm.RelationFilter.Bound = "all";
                        vm.RelationFilter.Kind = "all";
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relations-filters-closed"));
                        vm.IsRelationFiltersOpen = true;

                        vm.CurrentStage = UiStage.FfbTelemetry;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("ffb-telemetry"));

                        RenderCheckPanel(check, width, Name("check-cards"));
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                check.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    // The Settings page with only this section open.
    private static void ShowSettings(MainWindowViewModel vm, string id)
    {
        vm.CurrentStage = UiStage.Settings;
        foreach (SettingsSectionItem section in vm.SettingsSections)
            section.IsExpanded = section.Id == id;
        AvaloniaRenderThread.Pump();
    }

    private static void RenderCheckPanel(MainWindowViewModel vm, int width, string file)
    {
        CheckProblemsPanel measure = new() { DataContext = vm };
        double tall;
        using (HiddenWindow sized = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = measure } }, new Size(width, 6000)))
            tall = Math.Min(6000, Math.Ceiling(measure.DesiredSize.Height + 24));
        using HiddenWindow fitted = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = new CheckProblemsPanel { DataContext = vm } } },
            new Size(width, Math.Max(400, tall)));
        GateRender.Save(fitted, width, Math.Max(400, tall), 1, file);
    }
}
