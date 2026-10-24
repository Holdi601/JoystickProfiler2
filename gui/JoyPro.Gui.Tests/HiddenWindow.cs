using System;
using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace JoyPro.Gui.Tests;

// The main window's page in a root that is never shown on the desktop (a test window must never appear on the user's
// screen or take the focus): the page moves from its MainWindow into an embeddable root (a hidden native host), which
// runs Avalonia's own layout passes, so virtualizing lists learn their visible part and realize rows as on screen.
[SupportedOSPlatform("windows")]
internal sealed class HiddenWindow : IDisposable
{
    private readonly SizedRoot root;
    private readonly Size size;

    public Control Page { get; }

    public Window Window { get; }

    private HiddenWindow(Window window, Size size)
    {
        Window = window;
        this.size = size;
        Page = (Control)window.Content!;
        window.Content = null;
        root = new SizedRoot { DataContext = window.DataContext, Width = size.Width, Height = size.Height };
        // Styles and resources of the window (the page's look) come along.
        System.Collections.Generic.List<Avalonia.Styling.IStyle> styles = [.. window.Styles];
        window.Styles.Clear();
        foreach (Avalonia.Styling.IStyle style in styles)
            root.Styles.Add(style);
        foreach (System.Collections.Generic.KeyValuePair<object, object?> resource in window.Resources)
            root.Resources[resource.Key] = resource.Value;
        root.RequestedThemeVariant = window.RequestedThemeVariant;
        root.Content = Page;
        root.Prepare();
        // The render loop of the hidden host drives layout passes after changes, as on screen.
        root.StartRendering();
        Settle();
    }

    public static HiddenWindow Create(Window window, Size size) => new(window, size);

    public void Settle()
    {
        for (int i = 0; i < 8; i++)
        {
            AvaloniaRenderThread.Pump();
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            AvaloniaRenderThread.Pump();
            System.Threading.Thread.Sleep(10);
        }
    }

    // The page onto its background as a PNG in JOYPRO_GUI_PARITY_PREVIEW (nothing without it).
    public void Snap(string name)
    {
        string? folder = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(folder))
            return;
        Directory.CreateDirectory(folder);
        using RenderTargetBitmap bitmap = Render();
        bitmap.Save(Path.Combine(folder, name));
    }

    // The page onto its background (opaque, 1 pixel per unit), as the screen shows it.
    public RenderTargetBitmap Render()
    {
        PixelSize pixels = new((int)size.Width, (int)size.Height);
        IBrush background = Page.TryFindResource("JP.PageBackgroundBrush", Page.ActualThemeVariant, out object? found) && found is IBrush own
            ? own
            : new SolidColorBrush(Color.Parse("#202020"));
        using RenderTargetBitmap content = RenderOnBackground(Page, pixels, background, Settle);
        RenderTargetBitmap bitmap = new(pixels);
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        return bitmap;
    }

    // The control rendered while it carries the background (a Panel or Border without one gets it for the render):
    // text drawn onto a transparent bitmap gets the wrong antialiasing, dark text looks smeared.
    public static RenderTargetBitmap RenderOnBackground(Control page, PixelSize pixels, IBrush background, Action settle)
    {
        bool painted = false;
        if (page is Panel { Background: null } panel)
        {
            panel.Background = background;
            painted = true;
        }
        else if (page is Border { Background: null } border)
        {
            border.Background = background;
            painted = true;
        }
        RenderTargetBitmap content = new(pixels);
        try
        {
            if (painted)
                settle();
            content.Render(page);
        }
        finally
        {
            if (painted && page is Panel p)
                p.Background = null;
            else if (painted && page is Border b)
                b.Background = null;
        }
        return content;
    }

    // The size comes from Width/Height, not from the hidden native host.
    private sealed class SizedRoot : EmbeddableControlRoot
    {
        public SizedRoot() => EnforceClientSize = false;

        protected override System.Type StyleKeyOverride => typeof(EmbeddableControlRoot);
    }

    public void Dispose()
    {
        root.StopRendering();
        root.Content = null;
        root.DataContext = null;
        Window.DataContext = null;
        root.Dispose();
    }
}
