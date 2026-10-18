using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xunit;

namespace JoyPro.Gui.Tests;

// Opt-in probe for the trailer's 4K shots: which controls render in a RenderTargetBitmap of more than 96 dpi.
//   set JOYPRO_TRAILER_PROBE=<out dir> & dotnet test gui/JoyPro.Gui.Tests --filter TrailerRenderProbeTests
[SupportedOSPlatform("windows")]
public sealed class TrailerRenderProbeTests
{
    [Fact]
    public void RenderAtHighDpi()
    {
        string? outDir = System.Environment.GetEnvironmentVariable("JOYPRO_TRAILER_PROBE");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            foreach (double scale in new[] { 1.0, 2.0 })
            {
                StackPanel root = new() { Spacing = 20, Margin = new Thickness(20), Background = new SolidColorBrush(Color.Parse("#202838")) };
                root.Children.Add(new Border { Padding = new Thickness(10), BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(2), Child = new TextBlock { Text = "plain border" } });
                root.Children.Add(new Border { Padding = new Thickness(10), BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(2), BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"), Child = new TextBlock { Text = "border with box shadow" } });
                root.Children.Add(new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(12), BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(2), BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"), Child = new TextBlock { Text = "rounded border with box shadow" } });
                root.Children.Add(new LayoutTransformControl { LayoutTransform = new ScaleTransform(1.5, 1.5), Child = new TextBlock { Text = "layout transform" } });
                root.Children.Add(new Border { Opacity = 0.9, Child = new TextBlock { Text = "opacity 0.9" } });
                Grid overlay = new() { Height = 120 };
                overlay.Children.Add(new TextBlock { Text = "under the overlay" });
                overlay.Children.Add(new Border { ZIndex = 100, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Black, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(2), Padding = new Thickness(10), Child = new TextBlock { Text = "z-index overlay" } });
                root.Children.Add(overlay);
                Window window = new() { Content = root, Width = 500, Height = 500 };
                Size size = new(500, 500);
                window.Measure(size);
                window.Arrange(new Rect(size));
                root.Measure(size);
                root.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
                PixelSize pixels = new((int)(500 * scale), (int)(500 * scale));
                using RenderTargetBitmap bitmap = new(pixels, new Vector(96 * scale, 96 * scale));
                bitmap.Render(root);
                bitmap.Save(Path.Combine(outDir, "probe-" + scale + ".png"));
            }
        });
    }
}
