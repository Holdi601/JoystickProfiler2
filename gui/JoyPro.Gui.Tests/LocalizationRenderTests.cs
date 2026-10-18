using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The pages in other languages (docs/localization.md), to look at text length and clipping: German, Finnish, Russian,
// Japanese and Chinese at 1280x800 and 2267x1250. Renders only when JOYPRO_LOC_OUT names an output folder:
//   set JOYPRO_LOC_OUT=<work folder>\localization\renders & dotnet test gui/JoyPro.Gui.Tests --filter LocalizationRenderTests
// JOYPRO_LOC_LANGS (comma list) and JOYPRO_LOC_PAGES (Start,Settings,...) choose others. Files: <page>-<lang>-<width>.png
[SupportedOSPlatform("windows")]
public sealed class LocalizationRenderTests
{
    [Fact]
    public void RenderPagesInOtherLanguages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_LOC_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string[] languages = (Environment.GetEnvironmentVariable("JOYPRO_LOC_LANGS") is { Length: > 0 } l ? l : "de,fi,ru,ja,zh-Hans")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        UiStage[] pages = (Environment.GetEnvironmentVariable("JOYPRO_LOC_PAGES") is { Length: > 0 } p ? p : "Start,Settings")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Enum.Parse<UiStage>).ToArray();

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                foreach (string language in languages)
                {
                    vm.ApplyLanguageChoice(language);
                    Assert.Equal(language, Loc.Current.Code);
                    foreach (UiStage page in pages)
                    {
                        vm.CurrentStage = page;
                        AvaloniaRenderThread.Pump();
                        foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
                            UiDesignRenderTests.RenderWindow(vm, width, height, Path.Combine(outDir, page.ToString().ToLowerInvariant() + "-" + language + "-" + width + ".png"));
                    }
                }
            }
            finally
            {
                vm.ApplyLanguageChoice("en");
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }
}
