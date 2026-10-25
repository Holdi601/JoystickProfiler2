using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
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

// The text size of Settings > Appearance on top of Windows' display scaling, and the fonts of a theme: the main pages
// at 150 % and 200 % text with 150 % Windows scaling (a 1280x720 and a 1920x1080 screen laid out in 1200x760 and
// 1280x760 units), and with a serif and a wide font at 150 % text: no overlapping controls or chips, no text over a
// control, no cut text (LayoutChecks, the rules of gate I10). JOYPRO_TEXTSCALE_OUT=<folder> keeps the renders and a
// report.
[SupportedOSPlatform("windows")]
public sealed class TextScaleLayoutTests
{
    private static readonly UiStage[] Pages = [UiStage.Start, UiStage.Relations, UiStage.Binds, UiStage.Modifiers, UiStage.Layout, UiStage.Export, UiStage.Settings];

    public static IEnumerable<object[]> Cases() =>
    [
        ["text150", 1.5, ""],
        ["text200", 2.0, ""],
        ["serif150", 1.5, "Georgia"],
        ["wide150", 1.5, "Verdana"],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void MainPages_HaveNoOverlapsOrCutText(string name, double textScale, string font)
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_TEXTSCALE_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            if (font.Length > 0 && !ThemeService.IsInstalled(font))
                return;
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                ThemeService.ApplyTypography(textScale, new ThemeFonts { Ui = font });
                foreach ((double width, double height) in new[] { (1200.0, 760.0), (1280.0, 760.0) })
                {
                    foreach (UiStage stage in Pages)
                    {
                        vm.CurrentStage = stage;
                        using HiddenWindow window = GateRender.NewWindow(vm, width, height);
                        Settle(window, vm);
                        Control root = GateRender.Root(window);
                        List<Rect> boxes = [];
                        string where = name + " " + (int)width + "x" + (int)height + " " + stage;
                        foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height))
                        {
                            problems.Add(where + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b));
                            boxes.Add(ra);
                            boxes.Add(rb);
                        }
                        foreach ((Control item, Rect ir, Border frame, Rect _) in LayoutChecks.Spills(root, width, height))
                        {
                            problems.Add(where + ": " + LayoutChecks.Describe(item) + " sticks out of " + GateRender.Describe(frame));
                            boxes.Add(ir);
                        }
                        foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height))
                        {
                            problems.Add(where + ": '" + Gate.Short(text.Text ?? string.Empty, 50) + "' cut (needs " + need.ToString("0", CultureInfo.InvariantCulture) + ", has " + got.ToString("0", CultureInfo.InvariantCulture) + ")");
                            if (GateRender.BoundsInWindow(text, root) is Rect r)
                                boxes.Add(r);
                        }
                        if (!string.IsNullOrWhiteSpace(outDir))
                            GateRender.Save(window, width, height, 1, Path.Combine(outDir, name + "-" + (int)width + "-" + stage.ToString().ToLowerInvariant() + ".png"), boxes);
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                ThemeService.ApplyTypography(1.0, null);
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        if (!string.IsNullOrWhiteSpace(outDir))
            File.WriteAllText(Path.Combine(outDir, "report-" + name + ".txt"), string.Join(Environment.NewLine, problems));
        List<string> distinct = problems.Distinct().ToList();
        Assert.True(distinct.Count == 0, distinct.Count + " layout problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, distinct.Take(60)));
    }

    // At a big text size the page scrolls as a whole; picking a relation scrolls only the relation list, so the pane's
    // header (title, search, filters) stays in view.
    [Fact]
    public void PickingARelation_AtBigText_KeepsThePaneHeaderInView()
    {
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                ThemeService.ApplyTypography(2.0, null);
                vm.CurrentStage = UiStage.Relations;
                vm.SelectedRelation = null;
                using HiddenWindow window = GateRender.NewWindow(vm, 1200, 760);
                Settle(window, vm);
                Control root = GateRender.Root(window);
                ScrollViewer page = root.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PageScroller");
                page.Offset = default;
                Settle(window, vm);
                foreach (JoyPro.Gui.Models.RelationListItem relation in vm.Relations.Reverse().Take(3))
                {
                    vm.SelectedRelation = relation;
                    Settle(window, vm);
                    if (page.Offset.Y > 0.5)
                        problems.Add("picking '" + relation.Name + "' scrolled the page by " + page.Offset.Y.ToString("0"));
                    TextBox search = root.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RelationSearchBox");
                    if (GateRender.SeenRect(search, root, 1200, 760) == null)
                        problems.Add("picking '" + relation.Name + "' hid the relation search");
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                ThemeService.ApplyTypography(1.0, null);
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
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
