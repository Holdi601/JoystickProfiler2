using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Every main page (and the engine output panel) and the dialogs in the light and the dark theme: the text of every
// visible TextBlock, also the ones inside buttons, toggle buttons, chips and key caps, must stand out from what is
// really behind it. The page is rendered in a hidden root (never shown); the background of a text is the color the
// render shows most around its glyphs, the text color its brush with every opacity on the way. WCAG contrast: at least
// 4.5:1, 3:1 for large text (24 px, or 18.66 px from SemiBold up). Disabled controls are left out (WCAG: inactive).
// JOYPRO_CONTRAST_OUT=<folder> keeps every render and a contact sheet per theme (contact-light.png, contact-dark.png)
// plus contrast-report.txt with every text that was measured too low.
[SupportedOSPlatform("windows")]
public sealed class ThemeContrastTests
{
    private const int Width = 1600;
    private const int Height = 2000;

    [Fact]
    public void EveryPageAndDialog_HasReadableText_InLightAndDark()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_CONTRAST_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        StringBuilder report = new();

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            // The opt-in Statistics page with a full document (every chart and section).
            vm.ShowStatisticsPage = true;
            vm.Statistics.Apply(UsageRecordingTests.SampleStats);
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    List<(string Name, string File)> shots = [];
                    using (HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(Width, Height)))
                    {
                        foreach (UiStage stage in Enum.GetValues<UiStage>())
                        {
                            vm.CurrentStage = stage;
                            Settle(main, vm);
                            Check(main, theme, stage.ToString(), problems, report, outDir, shots);
                        }
                        // The engine output panel (the "Output" button of the bottom bar) over the Binds page.
                        vm.CurrentStage = UiStage.Binds;
                        vm.EngineOutputExpanded = true;
                        Settle(main, vm);
                        Check(main, theme, "Output", problems, report, outDir, shots);
                        vm.EngineOutputExpanded = false;
                    }
                    // Every settings section open, on a page tall enough to show them all.
                    using (HiddenWindow tall = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(Width, 9000)))
                    {
                        vm.CurrentStage = UiStage.Settings;
                        vm.ExpandAllSettingsCommand.Execute(null);
                        Settle(tall, vm);
                        Check(tall, theme, "SettingsAllOpen", problems, report, outDir, shots);
                    }

                    CheckWindow(new ConfirmWindow(new ConfirmRequest("Remove Old Pedals from your profile?",
                        "Every bind and modifier of this device is removed from the profile (the relations stay).", "Remove device", true)), new Size(560, 260), theme, "ConfirmDialog", problems, report, outDir, shots);
                    CheckWindow(new GameSetupWindow { DataContext = vm }, new Size(900, 700), theme, "GameSetupDialog", problems, report, outDir, shots);
                    CheckWindow(new ReplaceDeviceWindow { DataContext = vm }, new Size(1000, 760), theme, "ReplaceDeviceDialog", problems, report, outDir, shots);
                    // The setup wizard, every step (the steps show what the fake profile gives them).
                    SetupWizardViewModel wizard = vm.CreateSetupWizard();
                    for (int step = 0; step < 5; step++)
                    {
                        wizard.CurrentStep = step;
                        CheckWindow(new SetupWizardWindow { DataContext = wizard }, new Size(1100, 800), theme, "SetupWizardStep" + (step + 1), problems, report, outDir, shots);
                    }

                    if (!string.IsNullOrWhiteSpace(outDir))
                        ContactSheet(shots, Path.Combine(outDir, "contact-" + theme + ".png"));
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });

        if (!string.IsNullOrWhiteSpace(outDir))
            File.WriteAllText(Path.Combine(outDir, "contrast-report.txt"), report.ToString());
        Assert.True(problems.Count == 0, problems.Count + " text(s) with too little contrast:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(80)));
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#FFFFFF", "#2563EB", 5.17)]
    [InlineData("#FFFFFF", "#3B82F6", 3.68)]
    [InlineData("#808080", "#FFFFFF", 3.95)]
    public void Contrast_IsTheWcagRatio(string a, string b, double expected)
    {
        Assert.Equal(expected, Contrast(Color.Parse(a), Color.Parse(b)), 2);
    }

    private static void Settle(HiddenWindow main, MainWindowViewModel vm)
    {
        int quiet = 0;
        for (int i = 0; i < 300 && quiet < 3; i++)
        {
            main.Settle();
            quiet = vm.IsBusy ? 0 : quiet + 1;
            Thread.Sleep(10);
        }
        // Page changes fade and slide in: the render waits until they are over.
        for (int i = 0; i < 8; i++)
        {
            main.Settle();
            Thread.Sleep(60);
        }
    }

    private static void CheckWindow(Window window, Size size, string theme, string name, List<string> problems, StringBuilder report, string? outDir, List<(string, string)> shots)
    {
        using HiddenWindow host = HiddenWindow.Create(window, size);
        host.Settle();
        host.Settle();
        Check(host, theme, name, problems, report, outDir, shots);
    }

    private static void Check(HiddenWindow host, string theme, string name, List<string> problems, StringBuilder report, string? outDir, List<(string, string)> shots)
    {
        using RenderTargetBitmap bitmap = host.Render();
        PixelSize pixels = bitmap.PixelSize;
        int stride = pixels.Width * 4;
        byte[] buffer = new byte[stride * pixels.Height];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(pixels), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            string file = Path.Combine(outDir, name + "-" + theme + ".png");
            bitmap.Save(file);
            shots.Add((name, file));
        }

        Rect page = new(0, 0, pixels.Width, pixels.Height);
        HashSet<string> seen = [];
        foreach (TextBlock text in host.Page.GetVisualDescendants().OfType<TextBlock>())
        {
            string content = (text.Text ?? string.Empty).Trim();
            if (content.Length == 0 || !text.IsEffectivelyVisible || !text.IsEffectivelyEnabled || text.Bounds.Width < 2 || text.Bounds.Height < 2)
                continue;
            Rect? visible = VisibleRect(text, host.Page);
            if (visible is not Rect area)
                continue;
            Rect full = new(text.TranslatePoint(new Point(0, 0), host.Page) ?? default, text.Bounds.Size);
            area = area.Intersect(page);
            if (area.Width * area.Height < 0.6 * full.Width * full.Height || area.Width < 2 || area.Height < 2)
                continue;
            if (!TryForeground(text, out Color fg, out double alpha))
                continue;
            Color? bgOrNull = Background(buffer, stride, area, fg);
            if (bgOrNull is not Color bg)
                continue;
            Color shown = Blend(fg, bg, alpha);
            double ratio = Contrast(shown, bg);
            bool large = text.FontSize >= 24 || (text.FontSize >= 18.66 && (int)text.FontWeight >= (int)FontWeight.SemiBold);
            double needed = large ? 3.0 : 4.5;
            if (ratio + 0.005 >= needed)
                continue;
            string owner = Owner(text);
            string line = string.Format(CultureInfo.InvariantCulture, "{0} {1}: \"{2}\" ({3}, {4:0.#}px) {5} on {6} = {7:0.00} < {8:0.0}",
                theme, name, content.Length > 50 ? content[..50] + "..." : content, owner, text.FontSize, Hex(shown), Hex(bg), ratio, needed);
            report.AppendLine(line + string.Format(CultureInfo.InvariantCulture, " at {0:0},{1:0}", area.X, area.Y));
            if (seen.Add(theme + name + content + owner + Hex(shown) + Hex(bg)))
                problems.Add(line);
        }
    }

    // The part of the text inside every clipping ancestor (scroll viewers, clipped borders), in page coordinates.
    private static Rect? VisibleRect(Visual visual, Visual page)
    {
        Point? origin = visual.TranslatePoint(new Point(0, 0), page);
        if (origin == null)
            return null;
        Rect rect = new(origin.Value, visual.Bounds.Size);
        for (Visual? parent = visual.GetVisualParent(); parent != null && parent != page; parent = parent.GetVisualParent())
        {
            if (!parent.ClipToBounds)
                continue;
            Point? at = parent.TranslatePoint(new Point(0, 0), page);
            if (at == null)
                return null;
            rect = rect.Intersect(new Rect(at.Value, parent.Bounds.Size));
            if (rect.Width <= 0 || rect.Height <= 0)
                return null;
        }
        return rect;
    }

    // The text's color and how opaque it is drawn (its brush and every opacity up to the page).
    private static bool TryForeground(TextBlock text, out Color color, out double alpha)
    {
        color = default;
        alpha = 0;
        if (text.Foreground is not ISolidColorBrush brush)
            return false;
        color = brush.Color;
        alpha = color.A / 255.0 * brush.Opacity;
        for (Visual? v = text; v != null; v = v.GetVisualParent())
            alpha *= v.Opacity;
        color = Color.FromRgb(color.R, color.G, color.B);
        return alpha > 0.05;
    }

    // The color the render shows most in the text's box (the glyphs cover less of it); the second most when the most is
    // the text color itself (heavy, tight text).
    private static Color? Background(byte[] buffer, int stride, Rect area, Color fg)
    {
        Dictionary<int, (int Count, long R, long G, long B)> bins = [];
        int x0 = (int)Math.Floor(area.X), y0 = (int)Math.Floor(area.Y);
        int x1 = (int)Math.Ceiling(area.Right), y1 = (int)Math.Ceiling(area.Bottom);
        int width = stride / 4, height = buffer.Length / stride;
        x0 = Math.Clamp(x0, 0, width - 1);
        x1 = Math.Clamp(x1, 1, width);
        y0 = Math.Clamp(y0, 0, height - 1);
        y1 = Math.Clamp(y1, 1, height);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = y * stride + x * 4;
                byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                bins.TryGetValue(key, out var bin);
                bins[key] = (bin.Count + 1, bin.R + r, bin.G + g, bin.B + b);
            }
        }
        if (bins.Count == 0)
            return null;
        List<Color> top = bins.Values.OrderByDescending(v => v.Count).Take(2)
            .Select(v => Color.FromRgb((byte)(v.R / v.Count), (byte)(v.G / v.Count), (byte)(v.B / v.Count))).ToList();
        if (top.Count > 1 && Distance(top[0], fg) < 40)
            return top[1];
        return top[0];
    }

    private static double Distance(Color a, Color b) =>
        Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

    private static Color Blend(Color fg, Color bg, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return Color.FromRgb((byte)Math.Round(fg.R * alpha + bg.R * (1 - alpha)), (byte)Math.Round(fg.G * alpha + bg.G * (1 - alpha)), (byte)Math.Round(fg.B * alpha + bg.B * (1 - alpha)));
    }

    internal static double Contrast(Color a, Color b)
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

    // What the text belongs to: a button, toggle button, chip or key cap, else the text itself.
    private static string Owner(TextBlock text)
    {
        for (Visual? v = text.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            switch (v)
            {
                case ToggleButton toggle:
                    return toggle.GetType().Name + Classes(toggle);
                case Button button:
                    return "Button" + Classes(button);
                case Border border when border.Classes.Contains("chip"):
                    return "Chip" + Classes(border);
                case Border border when border.Classes.Contains("keycap"):
                    return "Keycap";
                case ListBoxItem:
                case ComboBox:
                case TextBox:
                    return v.GetType().Name;
            }
        }
        return "TextBlock" + Classes(text);
    }

    private static string Classes(StyledElement element)
    {
        string classes = string.Join(".", element.Classes.Where(c => !c.StartsWith(':')));
        return classes.Length == 0 ? string.Empty : "." + classes;
    }

    // All renders of a theme side by side, a third of their size.
    private static void ContactSheet(List<(string Name, string File)> shots, string file)
    {
        const int columns = 4;
        const double scale = 1.0 / 3;
        List<Bitmap> images = shots.Select(s => new Bitmap(s.File)).ToList();
        try
        {
            int cellW = (int)(images.Max(i => i.PixelSize.Width) * scale) + 12;
            int cellH = (int)(Math.Min(Height, images.Max(i => i.PixelSize.Height)) * scale) + 30;
            int rows = (images.Count + columns - 1) / columns;
            using RenderTargetBitmap sheet = new(new PixelSize(cellW * columns, cellH * rows));
            using (DrawingContext context = sheet.CreateDrawingContext())
            {
                context.FillRectangle(Brushes.DimGray, new Rect(0, 0, cellW * columns, cellH * rows));
                for (int i = 0; i < images.Count; i++)
                {
                    double x = i % columns * cellW + 6, y = i / columns * cellH + 24;
                    Bitmap image = images[i];
                    // A tall render (every settings section open) shows its first screen here; the file has all of it.
                    double shownHeight = Math.Min(Height, image.PixelSize.Height);
                    context.DrawImage(image, new Rect(0, 0, image.PixelSize.Width, shownHeight),
                        new Rect(x, y, image.PixelSize.Width * scale, shownHeight * scale));
                    context.DrawText(new FormattedText(shots[i].Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 16, Brushes.White),
                        new Point(x, y - 21));
                }
            }
            sheet.Save(file);
        }
        finally
        {
            foreach (Bitmap image in images)
                image.Dispose();
        }
    }
}
