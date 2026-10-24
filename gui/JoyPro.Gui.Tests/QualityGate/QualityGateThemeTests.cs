using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I8 of the quality gate (QualityGate.cs): the main pages rendered offscreen in Light and Dark with the real profile; every
// visible, enabled text (labels, button captions, list rows) is checked against the background it is drawn on (the
// visual tree's brushes composited, opacities included). A ratio below 3:1 (WCAG's large-text / UI minimum) is a finding.
// The pages are saved as contrast-<page>-<theme>.png next to the report.
[SupportedOSPlatform("windows")]
public sealed class QualityGateThemeTests
{
    private const double MinimumRatio = 3.0;
    private const int Width = 1600;
    private const int Height = 1000;

    private static readonly UiStage[] Pages = [UiStage.Start, UiStage.Relations, UiStage.Binds, UiStage.Modifiers, UiStage.Layout, UiStage.Export, UiStage.Settings, UiStage.Cockpits];

    [Fact]
    public void I8_TextContrastInLightAndDark()
    {
        if (!Gate.Enabled)
            return;
        string dir = Path.Combine(Gate.OutDir, "contrast");
        Directory.CreateDirectory(dir);
        Dictionary<string, (int Count, List<string> Examples, double Ratio, string Theme, string Page, string Fg, string Bg)> low = new(StringComparer.Ordinal);
        int checkedTexts = 0;
        GateSession.Run(Gate.CopyProfile("contrast"), async s =>
        {
            MainWindowViewModel vm = s.Vm;
            if (s.HasProfile)
                await vm.LoadProfileCommand.ExecuteAsync(null);
            // The Start page as the user sees it: the game cards of the installed games.
            await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
            await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (UiStage page in Pages)
                    {
                        vm.CurrentStage = page;
                        await Task.Delay(100);
                        await s.SettleAsync(() => true, 30000);
                        if (page == UiStage.Relations && vm.SelectedRelation == null && vm.Relations.Count > 0)
                        {
                            vm.SelectedRelation = vm.Relations[0];
                            await Task.Delay(100);
                            await s.SettleAsync(() => vm.RelationNodes.Count > 0, 30000);
                        }
                        HiddenWindow window = GateRender.NewWindow(vm, Width, Height);
                        try
                        {
                            foreach ((TextBlock text, double ratio, Color fg, Color bg) in Check(window))
                            {
                                checkedTexts++;
                                if (ratio >= MinimumRatio)
                                    continue;
                                string key = theme + "|" + page + "|" + Hex(fg) + "|" + Hex(bg);
                                string example = "'" + Gate.Short(text.Text ?? string.Empty, 40) + "' (" + Owner(text) + ")";
                                if (!low.TryGetValue(key, out var entry))
                                    entry = (0, [], ratio, theme, page.ToString(), Hex(fg), Hex(bg));
                                entry.Count++;
                                if (entry.Examples.Count < 4 && !entry.Examples.Contains(example))
                                    entry.Examples.Add(example);
                                entry.Ratio = Math.Min(entry.Ratio, ratio);
                                low[key] = entry;
                            }
                            GateRender.Save(window, Width, Height, 1.0, Path.Combine(dir, "contrast-" + page.ToString().ToLowerInvariant() + "-" + theme + ".png"));
                        }
                        finally
                        {
                            window.Dispose();
                            AvaloniaRenderThread.Pump();
                        }
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        foreach (var entry in low.Values.OrderBy(e => e.Ratio))
            Gate.Fail("I8", "text contrast below 3:1 (" + entry.Theme + ")", string.Empty, entry.Theme + " " + entry.Page + ": " + entry.Count + "x " + string.Join(", ", entry.Examples),
                entry.Ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1, text " + entry.Fg + " on " + entry.Bg);
        Gate.Note("I8: " + checkedTexts + " texts checked on " + Pages.Length + " pages in Light and Dark; pictures in " + dir);
        Gate.Finish("I8");
    }

    // Every text a person sees on the page: visible, enabled, inside the window, with something written.
    private static IEnumerable<(TextBlock Text, double Ratio, Color Fg, Color Bg)> Check(HiddenWindow window)
    {
        Control root = window.Page;
        Color page = ColorOf(root.TryFindResource("JP.PageBackgroundBrush", root.ActualThemeVariant, out object? found) && found is IBrush own ? own : null)
                     ?? (root.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        page = Color.FromRgb(page.R, page.G, page.B);
        foreach (TextBlock text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(text.Text) || !text.IsEffectivelyVisible || !text.IsEffectivelyEnabled || text.Bounds.Width < 1 || text.Bounds.Height < 1)
                continue;
            Point? at = text.TranslatePoint(new Point(1, 1), root);
            if (at == null || at.Value.X < 0 || at.Value.Y < 0 || at.Value.X > Width || at.Value.Y > Height)
                continue;
            if (!InsideClips(text, root))
                continue;
            Color? fgColor = ColorOf(text.Foreground);
            if (fgColor == null)
                continue;
            // A text drawn transparent or inside a faded-out part (a switch's other state, an animation's start) is not seen.
            double shown = fgColor.Value.A / 255.0;
            for (Visual? v = text; v != null && v != root.GetVisualParent(); v = v.GetVisualParent())
                shown *= v.Opacity;
            if (shown < 0.1)
                continue;
            // The layers under the text, nearest first, up to the first opaque one; the page background below them.
            List<(Color Color, double Alpha)> layers = [];
            double textOpacity = 1;
            bool opaque = false;
            for (Visual? v = text; v != null && v != root.GetVisualParent(); v = v.GetVisualParent())
            {
                IBrush? brush = v switch
                {
                    TextBlock t => t.Background,
                    Border b => b.Background,
                    Panel p => p.Background,
                    ContentPresenter cp => cp.Background,
                    _ => null
                };
                Color? c = ColorOf(brush);
                if (c != null && c.Value.A > 0)
                {
                    double alpha = c.Value.A / 255.0;
                    layers.Add((Color.FromRgb(c.Value.R, c.Value.G, c.Value.B), alpha));
                    if (alpha >= 0.999)
                    {
                        opaque = true;
                        break;
                    }
                }
                if (layers.Count == 0)
                    textOpacity *= v.Opacity;
            }
            Color bg = page;
            if (!opaque)
                layers.Add((page, 1));
            for (int i = layers.Count - 1; i >= 0; i--)
                bg = i == layers.Count - 1 ? layers[i].Color : Blend(layers[i].Color, bg, layers[i].Alpha);
            Color fg = Blend(Color.FromRgb(fgColor.Value.R, fgColor.Value.G, fgColor.Value.B), bg, fgColor.Value.A / 255.0 * textOpacity);
            yield return (text, Ratio(fg, bg), fg, bg);
        }
    }

    // Off-screen parts of a scroll viewer or a collapsed expander are no text a person sees now.
    private static bool InsideClips(Control text, Control root)
    {
        for (Visual? v = text.GetVisualParent(); v != null && v != root; v = v.GetVisualParent())
        {
            if (v is ScrollContentPresenter presenter && v is Control clip)
            {
                Point? a = text.TranslatePoint(new Point(0, 0), clip);
                if (a == null || a.Value.Y > clip.Bounds.Height || a.Value.Y + text.Bounds.Height < 0 || a.Value.X > clip.Bounds.Width || a.Value.X + text.Bounds.Width < 0)
                    return false;
            }
        }
        return true;
    }

    private static Color? ColorOf(IBrush? brush)
    {
        switch (brush)
        {
            case ISolidColorBrush solid:
                return Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B);
            case IGradientBrush gradient when gradient.GradientStops.Count > 0:
                {
                    double a = gradient.GradientStops.Average(s => (double)s.Color.A) * gradient.Opacity;
                    return Color.FromArgb((byte)Math.Round(a), (byte)gradient.GradientStops.Average(s => s.Color.R), (byte)gradient.GradientStops.Average(s => s.Color.G), (byte)gradient.GradientStops.Average(s => s.Color.B));
                }
            default:
                return null;
        }
    }

    private static Color Blend(Color top, Color bottom, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return Color.FromRgb((byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)), (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)), (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));
    }

    internal static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    // What the text belongs to: the nearest named control or button / chip around it.
    private static string Owner(TextBlock text)
    {
        for (Visual? v = text.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            if (v is Button or ToggleButton or CheckBox or RadioButton or ComboBox or TabItem or MenuItem)
            {
                string classes = v is StyledElement se && se.Classes.Count > 0 ? "." + string.Join(".", se.Classes.Where(c => !c.StartsWith(':'))) : string.Empty;
                return v.GetType().Name + classes;
            }
        }
        string own = text.Classes.Count > 0 ? "." + string.Join(".", text.Classes.Where(c => !c.StartsWith(':'))) : string.Empty;
        return "TextBlock" + own;
    }
}
