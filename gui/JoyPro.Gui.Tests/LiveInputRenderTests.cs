using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Live input and the device icon on the real window (docs/ui/live-input.md, docs/device-reference.md):
// - the icon test always runs: the Layout page offers the stick's pictures as icons, the chosen one goes to the
//   device cards, the bind list and back to automatic;
// - JOYPRO_LIVE_OUT=<folder> also renders the pages with live input (a held button, a moving axis) at 2267x1250 and
//   1280x800 in both themes: live-<page>-<width>-<theme>.png.
[SupportedOSPlatform("windows")]
public sealed class LiveInputRenderTests
{
    private const string Stick = AxisTuningTests.Stick;

    [Fact]
    public void DeviceIcon_ChosenOnTheLayoutPage_IsShownByTheCardsAndTheBindList()
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "live-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(work, "data"));
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLoadedVmAsync(work));
                try
                {
                    AvaloniaRenderThread.Wait(OpenOnLayoutPageAsync(vm, Stick));
                    AvaloniaRenderThread.Wait(vm.OpenDeviceIconPickerCommand.ExecuteAsync(null));
                    Assert.True(vm.IsDeviceIconPickerOpen);
                    Assert.Equal("Automatic", vm.DeviceIconOptions[0].Title);
                    Assert.True(vm.DeviceIconOptions[0].IsCurrent);
                    DeviceIconOption[] pictures = vm.DeviceIconOptions.Where(o => !o.IsAutomatic).ToArray();
                    Assert.NotEmpty(pictures);
                    Assert.All(pictures, o => Assert.NotNull(o.Preview));
                    Assert.All(pictures, o => Assert.True(o.Png!.Length > 500, o.Title));

                    DeviceIconOption chosen = pictures.Last();
                    vm.ChooseDeviceIconCommand.Execute(chosen);
                    AvaloniaRenderThread.Wait(WaitForAsync(() => vm.DeviceCards.Any(c => OverlayResolver.SameDevice(c.Device, Stick) && c.PictureSource.StartsWith("Your icon", StringComparison.Ordinal))));
                    DeviceCardItem card = vm.DeviceCards.Single(c => OverlayResolver.SameDevice(c.Device, Stick));
                    Assert.Equal("Your icon: " + chosen.Title, card.PictureSource);
                    Assert.Equal(chosen.Title, vm.WorkspaceDeviceIconTitle);
                    Assert.Same(card.Picture, vm.DeviceIconFor(Stick));
                    Assert.Contains(vm.Binds, b => OverlayResolver.SameDevice(b.Device, Stick) && b.HasDeviceIcon && ReferenceEquals(b.DeviceIcon, card.Picture));
                    Assert.Equal(chosen.Title, DeviceIconService.ChoiceOf(Stick)?.Title);

                    vm.ChooseDeviceIconCommand.Execute(vm.DeviceIconOptions[0]);
                    AvaloniaRenderThread.Wait(WaitForAsync(() => !card.PictureSource.StartsWith("Your icon", StringComparison.Ordinal)));
                    Assert.False(card.PictureSource.StartsWith("Your icon", StringComparison.Ordinal));
                    Assert.Null(DeviceIconService.ChoiceOf(Stick));
                    Assert.Equal("Automatic", vm.WorkspaceDeviceIconTitle);
                }
                finally
                {
                    vm.StopGameConfigWatcher();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
        }
    }

    [Fact]
    public void RenderLiveInputPages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_LIVE_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(outDir, "work");
        Directory.CreateDirectory(work);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(work, "data"));
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLoadedVmAsync(work));
                try
                {
                    foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                    {
                        Application.Current!.RequestedThemeVariant = variant;
                        foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
                        {
                            foreach (string page in new[] { "visualbind", "layout", "tables", "layoutfile", "follow", "iconpicker", "iconcards" })
                            {
                                AvaloniaRenderThread.Wait(ShowAsync(vm, page));
                                AvaloniaRenderThread.Pump();
                                RenderWindow(vm, width, height, Path.Combine(outDir, "live-" + page + "-" + width + "-" + theme + ".png"));
                            }
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
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
        }
    }

    // A held button and a moving axis on the stick: JOY_BTN2 (Weapon Release) down, X from the centre to the right.
    private static void PressAndMove(MainWindowViewModel vm, string button)
    {
        foreach (string held in vm.WorkspaceLiveInput.Pressed.ToList())
            vm.OnLiveInput(new JoystickInputEvent(Stick, held, false));
        vm.WorkspaceLiveInput.FrozenAt = null;
        vm.WorkspaceLiveInput.FrozenAt = vm.WorkspaceLiveInput.Now;
        vm.OnLiveInput(new JoystickInputEvent(Stick, button, true));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Stick, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Stick, [0.82, 0.5, 0, 0, 0, 0, 0, 0]));
    }

    private static async Task ShowAsync(MainWindowViewModel vm, string page)
    {
        vm.IsDeviceIconPickerOpen = false;
        vm.LayoutShowOverlays = false;
        vm.LayoutCalloutsEnabled = page == "tables";
        switch (page)
        {
            case "visualbind":
                vm.CurrentStage = UiStage.Binds;
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Gun Trigger");
                vm.OpenVisualAssignMode = true;
                vm.VisualAssignDevice = Stick;
                await vm.LoadVisualAssignCommand.ExecuteAsync(null);
                vm.LayoutShowsLayoutFile = false;
                await WaitForAsync(() => vm.HasReferenceModel);
                PressAndMove(vm, "JOY_BTN2");
                break;
            case "layout":
                vm.OpenVisualAssignMode = false;
                await OpenOnLayoutPageAsync(vm, Stick);
                vm.LayoutCanvasMode = DeviceCanvasMode.View;
                PressAndMove(vm, "JOY_BTN2");
                break;
            case "tables":
                await OpenOnLayoutPageAsync(vm, Stick);
                vm.LayoutCanvasMode = DeviceCanvasMode.View;
                PressAndMove(vm, "JOY_BTN2");
                break;
            case "layoutfile":
                await OpenOnLayoutPageAsync(vm, Stick);
                vm.LayoutShowsLayoutFile = true;
                vm.LayoutShowOverlays = true;
                vm.LayoutCanvasMode = DeviceCanvasMode.View;
                PressAndMove(vm, vm.LayoutEditorMarkers.FirstOrDefault(m => m.Token == "JOY_BTN_POV2_U")?.Token ?? "JOY_BTN2");
                break;
            case "follow":
                vm.OpenVisualAssignMode = false;
                vm.CurrentStage = UiStage.Binds;
                vm.FollowInputOnBinds = true;
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Pitch");
                vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", false));
                vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
                break;
            case "iconpicker":
                await OpenOnLayoutPageAsync(vm, Stick);
                vm.LayoutShowsLayoutFile = false;
                await vm.OpenDeviceIconPickerCommand.ExecuteAsync(null);
                break;
            case "iconcards":
                await OpenOnLayoutPageAsync(vm, Stick);
                vm.LayoutShowsLayoutFile = false;
                await vm.OpenDeviceIconPickerCommand.ExecuteAsync(null);
                DeviceIconOption? cel = vm.DeviceIconOptions.FirstOrDefault(o => o.Kind == "celart") ?? vm.DeviceIconOptions.LastOrDefault();
                vm.ChooseDeviceIconCommand.Execute(cel);
                await Task.Delay(300);
                vm.CurrentStage = UiStage.Start;
                break;
        }
    }

    private static async Task<MainWindowViewModel> CreateLoadedVmAsync(string work)
    {
        string picture = Path.Combine(work, "stand-in-device.png");
        if (!File.Exists(picture))
        {
            using RenderTargetBitmap bitmap = new(new PixelSize(1627, 2583));
            using (DrawingContext context = bitmap.CreateDrawingContext())
            {
                context.FillRectangle(new SolidColorBrush(Color.Parse("#5F5F5F")), new Rect(0, 0, 1627, 2583));
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2B2B2B")), null, new Rect(640, 300, 320, 1500), 120, 120);
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#1F1F1F")), null, new Point(800, 360), 220, 160);
            }
            bitmap.Save(picture);
        }
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(picture, work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        await vm.StartGameConfigWatcherAsync();
        vm.StatusText = "Ready.";
        return vm;
    }

    private static async Task OpenOnLayoutPageAsync(MainWindowViewModel vm, string device)
    {
        vm.OpenVisualAssignMode = false;
        vm.CurrentStage = UiStage.Layout;
        vm.IsCheatSheetsSection = false;
        if (vm.LayoutSuggestions.Count == 0)
            await vm.SuggestLayoutsCommand.ExecuteAsync(null);
        vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.FirstOrDefault(s => s.Device == device);
        await WaitForAsync(() => vm.LayoutEditorMarkers.Count > 0 && vm.DeviceReference.DeviceName == device && vm.HasReferenceModel);
        vm.ApplySelectedLayoutSuggestionsCommand.Execute(null);
        vm.LayoutShowsLayoutFile = false;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
    }

    private static void RenderWindow(MainWindowViewModel vm, int width, int height, string file)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? own) && own is IBrush brush
            ? brush
            : new SolidColorBrush(main.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }
}
