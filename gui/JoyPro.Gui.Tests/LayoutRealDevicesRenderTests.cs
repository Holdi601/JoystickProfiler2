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
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Layout page with the devices connected to THIS PC, through the real engine, rendered offscreen (no window): one
// picture per device, the way the user sees it after opening the page. Downloads the makers' photos (into
// JOYPRO_DEVICE_IMAGE_CACHE, never into the repo) and takes over JoyPro 1 layouts from a folder (a copy of the user's
// LayoutImages). The renders show the user's and makers' photos: keep them out of the repo.
// Runs only when JOYPRO_REAL_LAYOUT_OUT names an output folder and JOYPRO_REAL_LAYOUT_ENGINE the engine.exe; optional:
// JOYPRO_REAL_LAYOUT_OLD (JoyPro 1 layout folder copy), JOYPRO_REAL_LAYOUT_PROFILE (a .pr0file copy for the binds),
// JOYPRO_REAL_LAYOUT_CHOICES ("device name=model id;..." answers to "Which one is it?" for a second render).
[SupportedOSPlatform("windows")]
public sealed class LayoutRealDevicesRenderTests
{
    [Fact]
    public void RenderConnectedDevices()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_REAL_LAYOUT_OUT");
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_REAL_LAYOUT_ENGINE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
            return;
        Directory.CreateDirectory(outDir);
        string data = Path.Combine(outDir, "data");
        if (Directory.Exists(data))
            Directory.Delete(data, recursive: true);
        Directory.CreateDirectory(data);
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        string? oldCache = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE");
        bool blocked = DeviceImageCache.InternetPicturesBlocked;
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", Path.Combine(outDir, "image-cache"));
        DeviceImageCache.InternetPicturesBlocked = false;
        DeviceImageCache.InternetPicturesEnabled = true;
        DeviceReferenceService.Reload();
        StringBuilder report = new();
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
                try
                {
                    AvaloniaRenderThread.Wait(PrepareAsync(vm));
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    // The Start page's "Your devices" first: the same pictures as the Layout page.
                    AvaloniaRenderThread.Wait(RenderStartAsync(vm, Path.Combine(outDir, "start-before-answers.png"), report));
                    Dictionary<string, string> choices = ParseChoices(Environment.GetEnvironmentVariable("JOYPRO_REAL_LAYOUT_CHOICES"));
                    int index = 0;
                    foreach (LayoutSuggestionItem item in vm.LayoutSuggestions.ToList())
                    {
                        string device = item.Device;
                        string slug = (++index).ToString("D2") + "-" + Slug(item.DisplayName);
                        AvaloniaRenderThread.Wait(ShowAsync(vm, device));
                        report.AppendLine(Describe(vm, slug, device));
                        RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "layout-" + slug + ".png"));
                        // The labels at reading size (the user zooms in like this).
                        RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "layout-" + slug + "-zoom.png"), zoomSteps: vm.LayoutShowsLayoutFile ? 6 : 2);
                        string? answer = choices.FirstOrDefault(c => item.DisplayName.Contains(c.Key, StringComparison.OrdinalIgnoreCase)).Value;
                        if (answer != null && vm.DeviceReference.HasQuestion)
                        {
                            ChoiceCandidate? pick = vm.DeviceReference.QuestionCandidates.FirstOrDefault(c => c.Model.Id == answer)
                                                    ?? new ChoiceCandidate { Model = DeviceReferenceService.Catalog.Find(answer)!, Title = answer };
                            AvaloniaRenderThread.Wait(vm.DeviceReference.ChooseCandidateCommand.ExecuteAsync(pick));
                            AvaloniaRenderThread.Wait(SettleAsync(vm));
                            report.AppendLine(Describe(vm, slug + "-answered", device));
                            RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "layout-" + slug + "-answered.png"));
                            // The reference picture too, when the user's own layout is shown first.
                            if (vm.LayoutShowsLayoutFile)
                            {
                                vm.LayoutShowsLayoutFile = false;
                                AvaloniaRenderThread.Wait(SettleAsync(vm));
                                RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "layout-" + slug + "-answered-reference.png"));
                            }
                        }
                        else if (vm.LayoutShowsLayoutFile)
                        {
                            vm.LayoutShowsLayoutFile = false;
                            AvaloniaRenderThread.Wait(SettleAsync(vm));
                            report.AppendLine(Describe(vm, slug + "-reference", device));
                            RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "layout-" + slug + "-reference.png"));
                        }
                    }
                    AvaloniaRenderThread.Wait(RenderStartAsync(vm, Path.Combine(outDir, "start-after-answers.png"), report));
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
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", oldCache);
            DeviceImageCache.InternetPicturesBlocked = blocked;
            DeviceReferenceService.Reload();
        }
    }

    private static async Task PrepareAsync(MainWindowViewModel vm)
    {
        string? profile = Environment.GetEnvironmentVariable("JOYPRO_REAL_LAYOUT_PROFILE");
        if (!string.IsNullOrWhiteSpace(profile) && File.Exists(profile))
        {
            vm.ProfilePath = profile;
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
        }
        string? old = Environment.GetEnvironmentVariable("JOYPRO_REAL_LAYOUT_OLD");
        if (!string.IsNullOrWhiteSpace(old) && Directory.Exists(old))
            await vm.ImportOldJoyProLayoutsAsync(old);
        vm.CurrentStage = UiStage.Layout;
        vm.IsCheatSheetsSection = false;
        vm.LayoutSuggestIncludeConnected = true;
        await vm.SuggestLayoutsCommand.ExecuteAsync(null);
    }

    private static async Task RenderStartAsync(MainWindowViewModel vm, string file, StringBuilder report)
    {
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Start;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            AvaloniaRenderThread.Pump();
        }
        foreach (DeviceCardItem card in vm.DeviceCards)
            report.AppendLine("card | " + card.Device + " | " + card.Title + " | " + card.KindLabel + " | " + card.PictureSource + (card.NeedsGrip ? " | asks" : string.Empty));
        report.AppendLine("also connected | " + vm.OtherDevicesText);
        RenderWindow(vm, 1600, 1000, file);
        vm.CurrentStage = UiStage.Layout;
    }

    private static async Task ShowAsync(MainWindowViewModel vm, string device)
    {
        vm.LayoutCanvasMode = DeviceCanvasMode.View;
        await vm.ShowWorkspaceDeviceAsync(device);
        await SettleAsync(vm);
    }

    // Lets the picture downloads and the layout file load finish.
    private static async Task SettleAsync(MainWindowViewModel vm)
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            AvaloniaRenderThread.Pump();
        }
    }

    private static string Describe(MainWindowViewModel vm, string slug, string device)
    {
        DeviceReferenceViewModel r = vm.DeviceReference;
        return slug + " | " + device + " | model=" + (r.Model?.Id ?? "-") + " | picture=" + (vm.LayoutShowsLayoutFile ? "layout file " + Path.GetFileName(vm.LayoutEditorSourcePath) : r.SelectedPicture?.Title ?? "-") +
               " | known=" + r.KnownMarkerCount + "/" + r.Markers.Count + " | question=" + (r.Question == null ? "-" : r.Question.Kind + ":" + string.Join(",", r.QuestionCandidates.Take(4).Select(c => c.Model.Id + "(" + c.Score + ")"))) +
               " | pictures=" + string.Join(", ", vm.WorkspacePictures.Select(p => p.Title)) + " | status=" + vm.WorkspaceStatus;
    }

    private static Dictionary<string, string> ParseChoices(string? text)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
                map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return map;
    }

    private static string Slug(string text) => new(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    // zoomSteps > 0: the picture canvas zoomed in (+ pressed that often) to look at the labels at reading size.
    private static void RenderWindow(MainWindowViewModel vm, int width, int height, string file, int zoomSteps = 0)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        if (zoomSteps > 0 && Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<Views.Controls.DeviceMarkerCanvas>().FirstOrDefault(c => c.Name == "LayoutCanvas") is { } canvas)
        {
            for (int i = 0; i < zoomSteps; i++)
                canvas.ZoomIn();
            for (int pass = 0; pass < 3; pass++)
            {
                main.Measure(size);
                main.Arrange(new Rect(size));
                page.Measure(size);
                page.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
            }
        }
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? own) && own is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#202020"));
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
    }
}
