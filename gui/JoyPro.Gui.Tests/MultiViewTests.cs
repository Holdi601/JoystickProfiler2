using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Several views on one page (docs/device-reference.md "Several views on one page"): the page model (formats, Arrange,
// the file), the Layout page's page editor (start with the shown picture, add JoyPro's other pictures, page format,
// remove, saved per model), the cheat sheets using the page, and its look (JOYPRO_UI_DESIGN_OUT: multiview-*.png with
// the views drawn by the real engine).
[SupportedOSPlatform("windows")]
public sealed class MultiViewTests
{
    [Fact]
    public void Page_ArrangesTheViewsInsideTheFormat_AndRoundTrips()
    {
        MultiViewLayout page = new() { Model = "m", Page = "a4-portrait" };
        page.Items.Add(new MultiViewItem { Sheet = "a.json", PictureKey = "a", Title = "Front", Width = 3000, Height = 2000 });
        page.Items.Add(new MultiViewItem { Sheet = "b.json", PictureKey = "b", Title = "Side", Width = 1500, Height = 2500 });
        page.Arrange();
        foreach (MultiViewItem item in page.Items)
        {
            Assert.InRange(item.X, 0, page.Width);
            Assert.InRange(item.Y, 0, page.Height);
            Assert.True(item.X + item.Width * item.Scale <= page.Width + 1);
            Assert.True(item.Y + item.Height * item.Scale <= page.Height + 1);
        }
        // Portrait: the two views go one above the other (bigger than side by side).
        Assert.True(page.Items[1].Y > page.Items[0].Y + page.Items[0].Height * page.Items[0].Scale - 1);

        MultiViewLayout back = MultiViewLayout.Parse(page.ToJson());
        Assert.Equal("a4-portrait", back.Page);
        Assert.Equal(2480, back.Width);
        Assert.Equal(new[] { "Front", "Side" }, back.Items.Select(i => i.Title));
        Assert.Equal(page.Items[1].Scale, back.Items[1].Scale, 3);
        Assert.True(MultiViewLayout.IsMultiView(page.ToJson()));
        Assert.False(MultiViewLayout.IsMultiView("{\"format\":\"joypro-device-layout\"}"));
        Assert.Equal(7, MultiViewLayout.Pages.Count);
    }

    private static async Task<MainWindowViewModel> LoadedAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "multiview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(Path.Combine(work, "no-picture.png"), work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.SuggestLayoutsCommand.ExecuteAsync(null);
        vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.Single(s => s.Device == AxisTuningTests.Stick);
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.HasReferenceModel && vm.LayoutCallouts.HasSheet);
        vm.LayoutShowsLayoutFile = false;
        MultiViewStore.Delete(vm.DeviceReference.Model!.Id);
        return vm;
    }

    [Fact]
    public async Task LayoutPage_StartsWithTheShownPicture_AddsViews_FormatsAndSaves()
    {
        MainWindowViewModel vm = await LoadedAsync();
        string model = vm.DeviceReference.Model!.Id;
        Assert.Equal("Several views", vm.MultiViewButtonLabel);

        await vm.OpenMultiViewCommand.ExecuteAsync(null);
        Assert.True(vm.IsMultiViewOpen);
        MultiViewItemModel first = Assert.Single(vm.MultiViewItems);
        Assert.True(File.Exists(first.Item.Sheet));
        Assert.True(first.Width > 0 && first.Height > 0);
        Assert.NotEmpty(vm.MultiViewAddChoices);

        MultiViewAddChoice other = vm.MultiViewAddChoices.First();
        await vm.AddMultiViewCommand.ExecuteAsync(other);
        Assert.Equal(2, vm.MultiViewItems.Count);
        Assert.NotEqual(vm.MultiViewItems[0].Item.PictureKey, vm.MultiViewItems[1].Item.PictureKey);
        Assert.DoesNotContain(vm.MultiViewAddChoices, c => c.Title == other.Title);
        // The editor shows the picture it showed before.
        Assert.Equal(first.Item.PictureKey, vm.MultiViewItems[0].Item.PictureKey);

        vm.SelectedMultiViewPage = vm.MultiViewPages.Single(p => p.Id == "32x9");
        MultiViewLayout saved = MultiViewStore.Load(model)!;
        Assert.Equal("32x9", saved.Page);
        Assert.All(saved.Items, i => Assert.True(i.X >= 0 && i.X + i.Width * i.Scale <= 7681 && i.Y + i.Height * i.Scale <= 2161));

        // Moved on the canvas: saved.
        vm.MultiViewItems[1].X = 10;
        vm.MultiViewItemChanged(vm.MultiViewItems[1]);
        Assert.Equal(10, MultiViewStore.Load(model)!.Items[1].X);
        Assert.Equal("Page with 2 views", vm.MultiViewButtonLabel);

        // The cheat sheets print the page.
        string? sheet = DeviceReferenceService.CalloutFileOf(AxisTuningTests.Stick);
        Assert.Equal(MultiViewStore.FileOf(model), MultiViewStore.PageForSheet(sheet));

        vm.SelectedMultiViewItem = vm.MultiViewItems[1];
        vm.RemoveMultiViewCommand.Execute(null);
        Assert.Single(MultiViewStore.Load(model)!.Items);
        Assert.Null(MultiViewStore.PageForSheet(sheet));

        await vm.DeleteMultiViewPageCommand.ExecuteAsync(null);
        Assert.Null(MultiViewStore.Load(model));
        Assert.False(vm.IsMultiViewOpen);
    }

    // The look: two views drawn by the real engine on an A4 landscape page, the second selected.
    [Fact]
    public async Task LayoutPage_Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        string engine = Path.Combine(RepoRoot(), "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        if (string.IsNullOrWhiteSpace(outDir) || !File.Exists(engine))
            return;
        Directory.CreateDirectory(outDir);
        MainWindowViewModel vm = await LoadedAsync();
        await vm.OpenMultiViewCommand.ExecuteAsync(null);
        await vm.AddMultiViewCommand.ExecuteAsync(vm.MultiViewAddChoices.First());
        vm.SelectedMultiViewPage = vm.MultiViewPages.Single(p => p.Id == "a4-landscape");
        // The views as the engine draws them (the fake engine of the page does not draw).
        string views = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "multiview-views-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo start = new(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "layout", "multiview", MultiViewStore.FileOf(vm.DeviceReference.Model!.Id), "--views", views })
            start.ArgumentList.Add(arg);
        start.Environment["JOYPRO_DATA_DIR"] = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        using (Process process = Process.Start(start)!)
        {
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
        vm.CurrentStage = UiStage.Layout;
        vm.SelectedMultiViewItem = vm.MultiViewItems[1];
        vm.MultiViewStatus = "Click a view to select it; drag it, scale it with its corner or the mouse wheel.";
        AvaloniaRenderThread.Run(() =>
        {
            for (int i = 0; i < vm.MultiViewItems.Count; i++)
            {
                string png = Path.Combine(views, "view-" + i + ".png");
                Assert.True(File.Exists(png), png);
                using FileStream stream = File.OpenRead(png);
                vm.MultiViewItems[i].Image = new Bitmap(stream);
            }
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "multiview-" + w + ".png"));
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        });
        MultiViewStore.Delete(vm.DeviceReference.Model!.Id);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? string.Empty;
    }
}
