using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The screenshots of the website and wiki (website/README.md): every page of the bottom bar rendered offscreen as the
// whole window at 1280x800 in the light and the dark theme, plus taller renders of the main pages and the Check with a
// card. Same fake profile as the design renders (UiDesignRenderTests.DesignFakeEngine: relations of DCS,
// IL-2 Korea and War Thunder on a Warthog). The header shows a neutral demo path, never the temp folder of this PC.
// Opt-in, must run alone:
//   set JOYPRO_WEBSITE_SHOTS_OUT=<folder> & dotnet test gui/JoyPro.Gui.Tests --filter WebsiteScreenshotTests
// Files: <page>-<theme>.png (1280x800) and <page>-tall-<theme>.png (1280x<height>); website/build.py turns them into WebP.
[SupportedOSPlatform("windows")]
public sealed partial class WebsiteScreenshotTests
{
    // The path the header shows on every screenshot (the file need not exist: the fake engine answers every command).
    internal const string DemoProfilePath = @"C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file";

    internal static readonly (string Page, UiStage Stage)[] Pages =
    [
        ("start", UiStage.Start),
        ("relations", UiStage.Relations),
        ("binds", UiStage.Binds),
        ("modifiers", UiStage.Modifiers),
        // (layout: RenderWebsiteDetailScreenshots, with the layout renders' devices and a picture with markers)
        ("export", UiStage.Export),
        ("hud", UiStage.Hud),
        ("ffb", UiStage.FfbTelemetry),
        ("vjoy", UiStage.VirtualJoystick),
        ("cockpits", UiStage.Cockpits),
        ("statistics", UiStage.Statistics),
        ("settings", UiStage.Settings),
        ("advanced", UiStage.Advanced)
    ];

    private static readonly (string Page, UiStage Stage, int Height)[] TallPages =
    [
        ("start", UiStage.Start, 2000),
        ("relations", UiStage.Relations, 1400),
        ("binds", UiStage.Binds, 2300),
        ("export", UiStage.Export, 2600),
        ("settings", UiStage.Settings, 2600),
        ("advanced", UiStage.Advanced, 2000)
    ];

    [Fact]
    public void RenderWebsiteScreenshots()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_WEBSITE_SHOTS_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            string realProfile = vm.ProfilePath;
            try
            {
                vm.ProfilePath = DemoProfilePath;
                vm.ShowStatisticsPage = true;
                vm.StatusText = "Ready.";
                AvaloniaRenderThread.Pump();
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    vm.EngineOutputExpanded = false;
                    foreach ((string page, UiStage stage) in Pages)
                    {
                        vm.CurrentStage = stage;
                        AvaloniaRenderThread.Pump();
                        // The Export page lists the profile's games when it opens (an engine call): its tiles in the picture.
                        if (stage == UiStage.Export)
                            LoadExportTiles(vm, realProfile);
                        UiDesignRenderTests.RenderWindow(vm, 1280, 800, Path.Combine(outDir, page + "-" + theme + ".png"));
                    }
                    foreach ((string page, UiStage stage, int height) in TallPages)
                    {
                        vm.CurrentStage = stage;
                        AvaloniaRenderThread.Pump();
                        if (stage == UiStage.Export)
                            LoadExportTiles(vm, realProfile);
                        UiDesignRenderTests.RenderWindow(vm, 1280, height, Path.Combine(outDir, page + "-tall-" + theme + ".png"));
                    }

                    // The Check (the bottom bar's Check runs it and shows the Export page): one "Not written to the game" card.
                    // (an engine call needs the profile's real folder: the demo path is shown only in the pictures)
                    vm.ProfilePath = realProfile;
                    AvaloniaRenderThread.Wait(Done(vm.ValidateProfileCommand.ExecuteAsync(null)));
                    vm.ProfilePath = DemoProfilePath;
                    vm.ApplyExportMarks(
                    [
                        new ExportMarkItem
                        {
                            Game = "DCS", Aircraft = "F-16C_50", Relation = "Gear Up/Down", ActionId = "d68pnilunilcdnilvdnilvpnilvunil", Action = "Landing Gear Up/Down",
                            Device = "Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}", Input = "JOY_BTN22", GameActionId = "d3002pnilu3002cd2vd1vpnilvu0",
                            GameAction = "Weapon Release", Date = "2026-10-31"
                        }
                    ]);
                    vm.CurrentStage = UiStage.Export;
                    AvaloniaRenderThread.Pump();
                    UiDesignRenderTests.RenderWindow(vm, 1280, 1500, Path.Combine(outDir, "check-" + theme + ".png"));
                    // (The Output panel, the tabs, dialogs and wizard steps: RenderWebsiteDetailScreenshots.)
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });

        List<string> files = Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).OfType<string>().ToList();
        Assert.Contains("start-light.png", files);
        Assert.Contains("check-dark.png", files);
    }

    // The engine call behind the tiles needs the profile's real folder; the picture shows the demo path again.
    private static void LoadExportTiles(MainWindowViewModel vm, string realProfile)
    {
        vm.ProfilePath = realProfile;
        AvaloniaRenderThread.Wait(Done(vm.RefreshExportGamesCommand.ExecuteAsync(null)));
        Settle();
        vm.ProfilePath = DemoProfilePath;
        AvaloniaRenderThread.Pump();
    }

    private static async System.Threading.Tasks.Task<bool> Done(System.Threading.Tasks.Task task)
    {
        await task;
        return true;
    }
}
