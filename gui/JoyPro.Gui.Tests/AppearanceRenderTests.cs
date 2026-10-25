using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Settings > Appearance with the theme editor open (a theme of the user's own being edited: the token list, the fonts,
// the live preview, the contrast warning) and the main pages, in Light, Dark and a custom theme at 1280x800 and
// 2267x1250: nothing overlaps or is cut in the Appearance section, the preview shows the edited theme's colours.
// JOYPRO_APPEARANCE_OUT=<folder> keeps the renders.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AppearanceRenderTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "appearance-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public AppearanceRenderTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AppearanceSection_AndPages_InLightDarkAndACustomTheme()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_APPEARANCE_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            CustomTheme sand = ThemeTests.SampleTheme();
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark), ("custom", ThemeVariant.Light) })
                {
                    ThemeService.Apply(theme == "custom" ? "Custom" : "System", theme == "custom" ? sand : null);
                    Application.Current!.RequestedThemeVariant = variant;
                    // The whole theme editor (token list, fonts, contrast warning) on one tall page, once per theme.
                    foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250), (1280, 3600) })
                    {
                        foreach (UiStage stage in height > 2000 ? new[] { UiStage.Settings } : new[] { UiStage.Start, UiStage.Relations, UiStage.Layout, UiStage.Settings })
                        {
                            vm.CurrentStage = stage;
                            if (stage == UiStage.Settings)
                            {
                                // The Appearance section open, a theme in the editor (one colour unreadable: the warning shows).
                                vm.SettingsSearchText = "theme";
                                vm.NewThemeFromDarkCommand.Execute(null);
                                vm.ThemeTokenRows.First(r => r.Key == "JP.TextMutedBrush").Value = "#FF1A2436";
                            }
                            using HiddenWindow window = GateRender.NewWindow(vm, width, height);
                            if (stage == UiStage.Settings && window.Page.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "ThemeEditorExpander") is Expander editor)
                                editor.IsExpanded = true;
                            Settle(window, vm);
                            Control root = GateRender.Root(window);
                            // Every page carries its opaque background (text on transparency renders garbled).
                            double transparent = LayoutChecks.TransparentShare(root, width, height);
                            if (transparent > 0.0005)
                                problems.Add(theme + " " + width + " " + stage + ": " + (transparent * 100).ToString("0.0") + " % of the page drawn on transparency");
                            foreach ((Control item, Rect _, Border frame, Rect _) in LayoutChecks.Spills(root, width, height))
                                problems.Add(theme + " " + width + " " + stage + ": " + LayoutChecks.Describe(item) + " sticks out of " + GateRender.Describe(frame));
                            if (stage == UiStage.Settings)
                            {
                                Control? panel = root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "AppearancePanel");
                                if (panel == null)
                                    problems.Add(theme + " " + width + ": the Appearance section is not on the Settings page");
                                else
                                {
                                    foreach ((Control a, Rect _, Control b, Rect _) in LayoutChecks.Overlaps(root, width, height, panel))
                                        problems.Add(theme + " " + width + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b));
                                    foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, panel))
                                        problems.Add(theme + " " + width + ": '" + text.Text + "' cut");
                                }
                                Assert.True(vm.HasThemeContrastProblems);
                            }
                            if (!string.IsNullOrWhiteSpace(outDir))
                                GateRender.Save(window, width, height, 1, Path.Combine(outDir, theme + "-" + width + "-" + stage.ToString().ToLowerInvariant() + ".png"));
                        }
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                ThemeService.Apply("System", null);
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct().Take(40)));
    }

    private static void Settle(HiddenWindow window, MainWindowViewModel vm)
    {
        int quiet = 0;
        for (int i = 0; i < 300 && quiet < 3; i++)
        {
            window.Settle();
            quiet = vm.IsBusy ? 0 : quiet + 1;
            Thread.Sleep(10);
        }
        for (int i = 0; i < 6; i++)
        {
            window.Settle();
            Thread.Sleep(50);
        }
    }
}
