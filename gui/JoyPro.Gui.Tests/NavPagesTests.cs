using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Settings > Pages in the bottom bar (MainWindowViewModel.NavPages.cs): hide pages, put them in the user's order (drag,
// arrows), reset to JoyPro's order; Settings cannot be hidden; a hidden page another page opens shows in the bar while
// it is open; all of it kept in ui-settings.json for the next start. The tests that keep settings use the test run's own
// settings folder and run in the "TrayMode" collection that owns it. JOYPRO_V230_OUT names a folder for the renders
// (1280x800 and 2267x1250, light and dark).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class NavPagesTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public NavPagesTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose() => File.Delete(uiSettings);

    private static MainWindowViewModel Vm(bool persist) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    private static string[] Order(MainWindowViewModel vm) => vm.NavPages.Select(p => p.Id).ToArray();

    [Fact]
    public void Defaults_EveryPageOfTheBar_InJoyProsOrder_AllShown()
    {
        MainWindowViewModel vm = Vm(persist: false);
        Assert.Equal(["start", "relations", "binds", "modifiers", "layout", "export", "hud", "ffb", "vjoy", "cockpits", "statistics", "settings", "advanced", "check", "output"], Order(vm));
        Assert.All(vm.NavPages, p => Assert.True(p.IsVisible));
        // The Statistics page is opt-in: not in the bar (nor counted) until Settings > Usage statistics shows it.
        Assert.False(vm.FindNavPage("statistics")!.IsAvailable);
        Assert.False(vm.FindNavPage("statistics")!.IsShownInBar);
        Assert.Equal("All 14 pages shown", vm.NavPagesSummary);
        Assert.Contains(vm.SettingsSections, s => s.Id == "pages");
        Assert.True(vm.NavPages.First().IsFirst);
        Assert.True(vm.NavPages.Last().IsLast);
    }

    [Fact]
    public void Settings_CannotBeHidden()
    {
        MainWindowViewModel vm = Vm(persist: false);
        NavPageItem settings = vm.FindNavPage("settings")!;
        Assert.False(settings.CanHide);
        settings.IsVisible = false;
        Assert.True(settings.IsVisible);
        Assert.True(settings.IsShownInBar);
        // A saved file that lists it as hidden does not hide it either.
        vm.FindNavPage("hud")!.IsVisible = false;
        vm.ResetNavPagesCommand.Execute(null);
        Assert.All(vm.NavPages, p => Assert.True(p.IsVisible));
    }

    [Fact]
    public void Order_ArrowsAndMove_ResetBringsJoyProsOrderBack()
    {
        MainWindowViewModel vm = Vm(persist: false);
        NavPageItem layout = vm.FindNavPage("layout")!;
        vm.MoveNavPageUpCommand.Execute(layout);
        Assert.Equal(["start", "relations", "binds", "layout", "modifiers"], Order(vm).Take(5));
        vm.MoveNavPage(layout, 0);
        Assert.Equal("layout", Order(vm)[0]);
        Assert.True(layout.IsFirst);
        // The first page cannot go further up, the last one not further down.
        vm.MoveNavPageUpCommand.Execute(layout);
        Assert.Equal("layout", Order(vm)[0]);
        NavPageItem output = vm.FindNavPage("output")!;
        vm.MoveNavPageDownCommand.Execute(output);
        Assert.Equal("output", Order(vm)[^1]);
        vm.MoveNavPageDownCommand.Execute(layout);
        Assert.Equal(["start", "layout"], Order(vm).Take(2));
        vm.FindNavPage("cockpits")!.IsVisible = false;
        Assert.Equal("1 page hidden", vm.NavPagesSummary);

        vm.ResetNavPagesCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.NavPageDefaults.Select(d => d.Id), Order(vm));
        Assert.All(vm.NavPages, p => Assert.True(p.IsVisible));
    }

    [Fact]
    public void HiddenPage_OpenedFromAnotherPage_ShowsWhileOpen_AndCanBeShownForGood()
    {
        MainWindowViewModel vm = Vm(persist: false);
        NavPageItem modifiers = vm.FindNavPage("modifiers")!;
        modifiers.IsVisible = false;
        Assert.False(modifiers.IsShownInBar);
        Assert.False(vm.HasHiddenOpenPageHint);

        // "Open the Modifiers page" from another page: the page opens and its button shows while it is open.
        vm.CurrentStage = UiStage.Modifiers;
        Assert.True(modifiers.IsTemporarilyShown);
        Assert.True(modifiers.IsShownInBar);
        Assert.True(vm.HasHiddenOpenPageHint);
        Assert.Contains("Modifiers", vm.HiddenOpenPageHint);
        vm.CurrentStage = UiStage.Start;
        Assert.False(modifiers.IsShownInBar);
        Assert.False(vm.HasHiddenOpenPageHint);

        // Output opens its panel by itself (an error): shown while open.
        NavPageItem output = vm.FindNavPage("output")!;
        output.IsVisible = false;
        vm.EngineOutputExpanded = true;
        Assert.True(output.IsShownInBar);
        vm.EngineOutputExpanded = false;
        Assert.False(output.IsShownInBar);

        // "Show it always" from the hint.
        vm.CurrentStage = UiStage.Modifiers;
        vm.ShowOpenHiddenPageCommand.Execute(null);
        Assert.True(modifiers.IsVisible);
        Assert.False(modifiers.IsTemporarilyShown);
    }

    [Fact]
    public void OrderAndHiddenPages_AreKeptForTheNextStart()
    {
        MainWindowViewModel first = Vm(persist: true);
        first.MoveNavPage(first.FindNavPage("export")!, 1);
        first.FindNavPage("advanced")!.IsVisible = false;
        first.FindNavPage("vjoy")!.IsVisible = false;
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(uiSettings)))
        {
            Assert.Equal("export", json.RootElement.GetProperty("navPageOrder")[1].GetString());
            Assert.Equal(["vjoy", "advanced"], json.RootElement.GetProperty("hiddenNavPages").EnumerateArray().Select(e => e.GetString()));
        }

        MainWindowViewModel second = Vm(persist: true);
        Assert.Equal(["start", "export", "relations"], Order(second).Take(3));
        Assert.False(second.FindNavPage("advanced")!.IsVisible);
        Assert.False(second.FindNavPage("vjoy")!.IsVisible);
        Assert.True(second.FindNavPage("settings")!.IsVisible);
        Assert.Equal("2 pages hidden", second.NavPagesSummary);

        // Reset is kept too.
        second.ResetNavPagesCommand.Execute(null);
        MainWindowViewModel third = Vm(persist: true);
        Assert.Equal(MainWindowViewModel.NavPageDefaults.Select(d => d.Id), Order(third));
        Assert.All(third.NavPages, p => Assert.True(p.IsVisible));
    }

    // The window's bar follows the list: the user's order, hidden pages left out, a hidden open page shown; the rows of
    // Settings > Pages move by drag (the row under the pointer).
    [Fact]
    public void Bar_FollowsTheList_AndTheRowsMoveByDrag()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = Vm(persist: false);
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
            MainWindow window = (MainWindow)main.Window;
            Assert.Equal(MainWindowViewModel.NavPageDefaults.Select(d => d.Id).Where(id => id != "statistics"), window.NavBarOrder());

            vm.MoveNavPage(vm.FindNavPage("layout")!, 0);
            vm.FindNavPage("cockpits")!.IsVisible = false;
            vm.FindNavPage("hud")!.IsVisible = false;
            main.Settle();
            string[] bar = window.NavBarOrder().ToArray();
            Assert.Equal("layout", bar[0]);
            Assert.DoesNotContain("cockpits", bar);
            Assert.DoesNotContain("hud", bar);
            Assert.Contains("settings", bar);

            vm.CurrentStage = UiStage.Cockpits;
            main.Settle();
            Assert.Contains("cockpits", window.NavBarOrder());
            vm.CurrentStage = UiStage.Start;
            main.Settle();
            Assert.DoesNotContain("cockpits", window.NavBarOrder());

            // The list on the Settings page: a drag to the third row moves the page there.
            vm.CurrentStage = UiStage.Settings;
            vm.FindSettingsSection("pages")!.IsExpanded = true;
            main.Settle();
            Avalonia.Controls.ItemsControl list = window.FindControl<Avalonia.Controls.ItemsControl>("NavPagesList")!;
            Assert.True(list.GetRealizedContainers().Count() >= 14);
            double thirdRow = list.ContainerFromIndex(2)!.Bounds.Center.Y;
            Assert.Equal(2, window.NavPageIndexAt(thirdRow));
            Assert.Equal(14, window.NavPageIndexAt(100000));
        });
    }

    // Renders of the new parts (looked at by hand): Settings > Pages, and the bar with a hidden page open.
    [Fact]
    public void Render_SettingsPagesAndTheBar()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_V230_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
                    {
                        MainWindowViewModel vm = Vm(persist: false);
                        vm.FindNavPage("cockpits")!.IsVisible = false;
                        vm.FindNavPage("vjoy")!.IsVisible = false;
                        vm.MoveNavPage(vm.FindNavPage("layout")!, 1);
                        vm.CurrentStage = UiStage.Settings;
                        foreach (SettingsSectionItem section in vm.SettingsSections)
                            section.IsExpanded = section.Id == "pages";
                        vm.MoveSettingsSection(vm.FindSettingsSection("pages")!, 0);
                        using HiddenWindow main = Show(vm, variant, width, height);
                        main.Settle();
                        Save(main, Path.Combine(outDir, "settings-pages-" + width + "-" + theme + ".png"));
                        // A hidden page opened from elsewhere: it shows in the bar with the hint.
                        vm.CurrentStage = UiStage.Cockpits;
                        main.Settle();
                        Save(main, Path.Combine(outDir, "bar-hidden-open-" + width + "-" + theme + ".png"));
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    // The page in the theme of the render (the view model's own theme choice would put the system's back).
    internal static HiddenWindow Show(MainWindowViewModel vm, ThemeVariant variant, int width, int height)
    {
        vm.ThemeMode = variant == ThemeVariant.Light ? "Light" : "Dark";
        Application.Current!.RequestedThemeVariant = variant;
        AvaloniaRenderThread.Pump();
        return HiddenWindow.Create(new MainWindow { DataContext = vm, RequestedThemeVariant = variant }, new Size(width, height));
    }

    internal static void Save(HiddenWindow main, string file)
    {
        using RenderTargetBitmap bitmap = main.Render();
        bitmap.Save(file);
    }
}
