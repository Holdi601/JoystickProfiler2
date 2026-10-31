using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.5.0 export/import additions rendered offscreen at 1280 and 2267 wide, light and dark: the watcher hint in the
// import, the "not exported" chip on an action, the Move panel's line, the Check card with its fixes, Settings > Games
// (vJoy) and Confirmations, the Export page's modes and options, and the first-export question. Opt-in (needs the whole
// application, must run alone): set JOYPRO_A2_RENDER_OUT=<folder> & dotnet test --filter ExportDefaultsRenderTests
[SupportedOSPlatform("windows")]
public sealed class ExportDefaultsRenderTests
{
    [Fact]
    public void RenderExportDefaults()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_A2_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                RelationNodeItem node = vm.RelationNodes.First();
                vm.ApplyExportMarks(
                [
                    new ExportMarkItem
                    {
                        Game = node.Game, Aircraft = node.ActivePlanesText.Split(',').FirstOrDefault() ?? string.Empty, Relation = vm.SelectedRelation!.Name, ActionId = node.Id,
                        Action = node.TitlesSummary, Device = "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}", Input = "JOY_BTN5",
                        GameActionId = "d3001pnilu3001cd2vd1vpnilvu0", GameAction = "Weapon Release", Date = "2026-10-31"
                    }
                ]);
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    RenderDialog(new ConfirmWindow(new ConfirmRequest("First export to DCS World",
                        "Export replaces the binds in DCS World with your profile (a backup is made, Undo export brings them back). Import first?", "Export anyway", false)
                        { SecondaryLabel = "Import first", DontAskKey = MainWindowViewModel.ConfirmFirstExportKey }), Path.Combine(outDir, "first-export-question-" + theme + ".png"));
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        int height = width == 1280 ? 800 : 1300;
                        string Name(string page) => Path.Combine(outDir, page + "-" + width + "-" + theme + ".png");

                        // Start page: the import of DCS (in the profile, not watched) with the watcher hint.
                        vm.ListPrefs.WatcherHintShownGames.Clear();
                        GameCardItem dcs = vm.GameCards.Single(c => c.GameId == "DCS");
                        dcs.Watch = false;
                        vm.CurrentStage = UiStage.Start;
                        vm.StartImportWizardCommand.Execute(dcs);
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 1900, Name("import-watcher-hint"));
                        vm.ImportWizardBackCommand.Execute(null);

                        // Relations: the chip on the action; the Move panel in merge mode.
                        vm.CurrentStage = UiStage.Relations;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("relation-chip"));
                        vm.ExportMode = "merge";
                        vm.OpenMoveActionForNode(node);
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, height, Name("move-panel-merge"));
                        vm.CancelMoveActionCommand.Execute(null);
                        vm.ExportMode = "clean";

                        // Export page (modes, options, the Check card), tall to reach the check.
                        vm.CurrentStage = UiStage.Export;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 3400, Name("export-page"));

                        // Settings: Games (vJoy) and Confirmations open.
                        foreach (SettingsSectionItem section in vm.SettingsSections)
                            section.IsExpanded = section.Id is "games" or "confirmations";
                        vm.CurrentStage = UiStage.Settings;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 2400, Name("settings"));
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    internal static void RenderDialog(Avalonia.Controls.Window window, string file)
    {
        const int width = 600, height = 230;
        window.Width = width;
        window.Height = height;
        Size size = new(width, height);
        Avalonia.Controls.Control page = (Avalonia.Controls.Control)window.Content!;
        for (int pass = 0; pass < 6; pass++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        Avalonia.Media.IBrush background = window.TryFindResource("JP.PageBackgroundBrush", window.ActualThemeVariant, out object? found) && found is Avalonia.Media.IBrush own
            ? own
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#808080"));
        using Avalonia.Media.Imaging.RenderTargetBitmap content = new(new PixelSize(width, height));
        // Text on a transparent target smears in the light theme: the content gets the page background itself.
        if (page is Panel panel && panel.Background == null)
        {
            panel.Background = background;
            for (int pass = 0; pass < 2; pass++)
            {
                page.Measure(size);
                page.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
            }
        }
        content.Render(page);
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (Avalonia.Media.DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
    }
}
