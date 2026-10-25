using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tester report (2.3.1): "Create support file" opened a window taller than the screen that could not scroll or be
// resized; only closing it worked. Every dialog is now fitted to the screen's working area (WindowFit), scrolls its
// content and keeps its buttons in a footer below the scrolling part. Each dialog is laid out (never shown) at the
// working area of a 1280 x 720 screen at 150 % (taskbar 48 px): it fits, and every footer button lies fully inside it.
// JOYPRO_DIALOG_FIT_OUT=<folder> keeps a PNG per dialog.
[SupportedOSPlatform("windows")]
public sealed class DialogFitTests
{
    private static readonly PixelRect WorkingArea = new(0, 0, 1280, 720 - 48);
    private const double Scaling = 1.5;

    private static Size Fit => WindowFit.ClientSizeFor(WorkingArea, Scaling);

    [Fact]
    public void ClientSize_IsTheWorkingAreaInUnits_WithoutTitleBarAndFrame()
    {
        Size fit = Fit;
        Assert.Equal(1280 / 1.5 - WindowFit.DecorationWidth, fit.Width, 3);
        Assert.Equal(672 / 1.5 - WindowFit.DecorationHeight, fit.Height, 3);
    }

    [Fact]
    public void EveryDialog_FitsA1280x720ScreenAt150Percent_AndItsButtonsAreReachable()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_DIALOG_FIT_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                // The support file's question with its full list (40 entries and "... and n more").
                string message = "JoyPro saves one zip on this PC; nothing is sent. It holds:\n" +
                                 string.Join("\n", Enumerable.Range(1, 40).Select(i => "logs/engine-" + i + ".log (JoyPro log)")) + "\n... and 12 more";
                Check(() => new ConfirmWindow(new ConfirmRequest("Create support file?", message, "Save support file", false)), "confirm-support-file",
                    ["ConfirmButton", "CancelButton"], "MessageScroll", mustScroll: true, problems, outDir);
                Check(() => new ConfirmWindow(new ConfirmRequest("Remove Old Pedals from your profile?", "Every bind of this device is removed.", "Remove device", true) { DontAskKey = "remove-device" }),
                    "confirm-short", ["ConfirmButton", "CancelButton"], "MessageScroll", mustScroll: false, problems, outDir);
                Check(() => new GameSetupWindow { DataContext = vm }, "game-setup", ["SetupFooter"], "SetupScroll", mustScroll: false, problems, outDir);
                Check(() => new ReplaceDeviceWindow { DataContext = vm }, "replace-device", ["ReplaceFooter"], "ReplaceScroll", mustScroll: false, problems, outDir);
                SetupWizardViewModel wizard = vm.CreateSetupWizard();
                for (int step = 0; step < 5; step++)
                {
                    wizard.CurrentStep = step;
                    Check(() => new SetupWizardWindow { DataContext = wizard }, "setup-wizard-step" + (step + 1), ["WizardFooter"], null, mustScroll: false, problems, outDir);
                }

                // The main window's minimum size fits too (MainWindow.FitIntoScreen).
                MainWindow main = new();
                WindowFit.Apply(main, Fit);
                if (main.MinWidth > Fit.Width || main.MinHeight > Fit.Height || main.Width > Fit.Width || main.Height > Fit.Height)
                    problems.Add("main window: " + main.Width + " x " + main.Height + " (min " + main.MinWidth + " x " + main.MinHeight + ") > " + Fit);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // The window fitted as WindowFit does it before it shows, laid out at the size it then gets (a window sized by its
    // content: its content's height, at most the working area).
    private static void Check(Func<Window> create, string name, string[] footer, string? scrollName, bool mustScroll, List<string> problems, string? outDir)
    {
        Size fit = Fit;
        Window window = create();
        WindowFit.Apply(window, fit);
        if (window.MinWidth > fit.Width || window.MinHeight > fit.Height)
            problems.Add(name + ": minimum size " + window.MinWidth + " x " + window.MinHeight + " is larger than " + fit);
        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight && window.MaxHeight > fit.Height)
            problems.Add(name + ": a window sized by its content may grow past the screen (MaxHeight " + window.MaxHeight + ")");
        if (window.SizeToContent == SizeToContent.Manual && !window.CanResize)
            problems.Add(name + ": cannot be resized");
        if (window.MinHeight < 150 && window.CanResize)
            problems.Add(name + ": MinHeight " + window.MinHeight + " lets the buttons be squeezed away");

        double width = double.IsNaN(window.Width) ? fit.Width : Math.Min(window.Width, fit.Width);
        double height = double.IsNaN(window.Height) || window.SizeToContent != SizeToContent.Manual ? fit.Height : Math.Min(window.Height, fit.Height);
        Size size = new(width, height);
        if (window.SizeToContent != SizeToContent.Manual)
        {
            // As Windows sizes it: the content's own height, at most the working area (a fresh window is laid out then).
            double wanted;
            using (HiddenWindow probe = HiddenWindow.Create(window, size))
            {
                probe.Page.Measure(new Size(width, double.PositiveInfinity));
                wanted = probe.Page.DesiredSize.Height;
            }
            size = new Size(width, Math.Max(window.MinHeight, Math.Min(wanted, window.MaxHeight)));
            window = create();
            WindowFit.Apply(window, fit);
        }

        using HiddenWindow host = HiddenWindow.Create(window, size);
        host.Settle();
        host.Settle();
        Rect area = new(size);
        List<Button> buttons = [];
        foreach (string part in footer)
        {
            Control? control = window.FindControl<Control>(part);
            if (control == null)
            {
                problems.Add(name + ": no " + part);
                continue;
            }
            if (control.GetVisualAncestors().OfType<ScrollViewer>().Any())
                problems.Add(name + ": " + part + " scrolls away with the content (it must be in the footer)");
            if (control is Button button)
                buttons.Add(button);
            else
                buttons.AddRange(control.GetVisualDescendants().OfType<Button>());
        }
        foreach (Button button in buttons.Where(b => b.IsEffectivelyVisible))
        {
            Point? topLeft = button.TranslatePoint(new Point(0, 0), host.Page);
            Rect bounds = topLeft == null ? default : new Rect(topLeft.Value, button.Bounds.Size);
            if (topLeft == null || bounds.Width < 1 || bounds.Height < 1 || !area.Contains(bounds))
                problems.Add(name + ": button '" + button.Content + "' at " + bounds + " is not inside the window " + area);
        }
        if (buttons.Count(b => b.IsEffectivelyVisible) == 0)
            problems.Add(name + ": no visible button in the footer");
        if (scrollName != null)
        {
            ScrollViewer? scroll = window.FindControl<ScrollViewer>(scrollName);
            if (scroll == null)
                problems.Add(name + ": no " + scrollName);
            else if (mustScroll && scroll.Extent.Height <= scroll.Viewport.Height + 1)
                problems.Add(name + ": the content does not scroll (extent " + scroll.Extent.Height + ", viewport " + scroll.Viewport.Height + ")");
        }
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = host.Render();
            bitmap.Save(Path.Combine(outDir, name + "-1280x720-150.png"));
        }
    }
}
