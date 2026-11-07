using System;
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
using Avalonia.VisualTree;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The axis response's "Applies to" on the tester's case (a helicopter collective: AH-64D CPG / Pilot, Ka-50 III, SA342
// Copilot / Pilot), dark and light, 1280 and 1920 wide, cut to the section; the texts in texts.txt. Real engine; only when
// JOYPRO_265C_RENDER_OUT names the folder.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AxisTuningAppliesToRenderTests
{
    [Fact]
    public void Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_265C_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir) || SameMeaningRealEngineTests.Engine() == null)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder texts = new();
        AvaloniaRenderThread.Run(() =>
        {
            AxisTuningAppliesToTests.Setup setup = AvaloniaRenderThread.Wait(AxisTuningAppliesToTests.CreateAsync())!;
            try
            {
                // DCS has a curve; the two Apache seats share one of their own, the SA342 Copilot has another.
                AvaloniaRenderThread.Wait(setup.RunAsync("binds", "tuning", "set", AxisTuningAppliesToTests.Collective, "--scope", "game:DCS", "--deadzone", "0.05"));
                AvaloniaRenderThread.Wait(setup.RunAsync("binds", "tuning", "set", AxisTuningAppliesToTests.Collective, "--scope", "plane:DCS:AH-64D_BLK_II_CPG",
                    "--scope", "plane:DCS:AH-64D_BLK_II_PLT", "--deadzone", "0.12", "--curvature", "0.2"));
                AvaloniaRenderThread.Wait(setup.RunAsync("binds", "tuning", "set", AxisTuningAppliesToTests.Collective, "--scope", "plane:DCS:SA342 Copilot", "--deadzone", "0.08", "--slider", "true"));
                MainWindowViewModel vm = AvaloniaRenderThread.Wait(setup.OpenAsync());

                void Shot(string name)
                {
                    AvaloniaRenderThread.Wait(AxisTuningAppliesToTests.SettleAsync(vm));
                    texts.AppendLine("--- " + name);
                    foreach (var row in vm.AxisTuningScopes.Where(r => r.IsShown))
                        texts.AppendLine((row.IsChecked ? "[x] " : "[ ] ") + (row.IsAircraft ? "    " : string.Empty) + row.Label + "  |  " + row.UsesText + "  |  tip: " + row.UsesTip);
                    texts.AppendLine("groups: " + string.Join(" || ", vm.AxisTuningGroups.Select(g => (g.IsCurrent ? "*" : string.Empty) + g.Text)));
                    texts.AppendLine("status: " + vm.AxisTuningStatus);
                    texts.AppendLine("plan: " + vm.AxisTuningSavePlanText);
                    texts.AppendLine("replace: " + vm.AxisTuningReplaceText);
                    texts.AppendLine("buttons: " + vm.AxisTuningSaveText + " / " + vm.AxisTuningClearText + " (clear " + vm.CanClearAxisTuning + ")");
                    foreach (int width in new[] { 1280, 1920 })
                    {
                        foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                        {
                            Application.Current!.RequestedThemeVariant = variant;
                            RenderSection(vm, width, Path.Combine(outDir, name + "-" + width + "-" + theme + ".png"));
                        }
                    }
                }

                // Opened: DCS's aircraft that use its curve are ticked; the ones with their own are not.
                Shot("1-default");
                // The SA342 Copilot (own curve) ticked as well: said in orange before saving.
                vm.AxisTuningScopes.Single(s => s.Scope == "plane:DCS:SA342 Copilot").IsChecked = true;
                Shot("2-replaces-own");
                // The Apache group clicked: a curve for those two.
                vm.TickAxisTuningGroupCommand.Execute(vm.AxisTuningGroups.First(g => g.Text.Contains("same curve", StringComparison.Ordinal)));
                Shot("3-group-two-of-five");
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                AvaloniaRenderThread.Wait(setup.DisposeAsync().AsTask());
                File.WriteAllText(Path.Combine(outDir, "texts.txt"), texts.ToString());
            }
        });
    }

    // The whole window at this width, cut to the axis response section.
    private static void RenderSection(MainWindowViewModel vm, int width, string file)
    {
        const int height = 3400;
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        RenderPanels.UnvirtualizeItemsControls(main);
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        Control panel = page.GetVisualDescendants().OfType<Control>().First(c => c.Name == "AxisResponsePanel");
        Point top = panel.TranslatePoint(new Point(0, 0), page) ?? new Point(0, 0);
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? own) && own is IBrush brush
            ? brush
            : new SolidColorBrush(main.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        int x = (int)Math.Max(0, top.X - 12);
        int y = (int)Math.Max(0, top.Y - 12);
        PixelSize cut = new((int)Math.Min(width - x, panel.Bounds.Width + 24), (int)Math.Min(height - y, panel.Bounds.Height + 24));
        using RenderTargetBitmap bitmap = new(cut);
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(0, 0, cut.Width, cut.Height));
            context.DrawImage(content, new Rect(x, y, cut.Width, cut.Height), new Rect(0, 0, cut.Width, cut.Height));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }
}
