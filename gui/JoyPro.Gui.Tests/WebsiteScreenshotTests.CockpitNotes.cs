using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.1: the Cockpits page's stick page (the design profile's selected aircraft, JoyPro's own drawing) was drawn taller
// than its control (stretched wider than it measured), so the notes, photos and sources below it were drawn over its
// HOTAS table. The page now stays inside its control and the notes start below it, in the light and the dark theme.
// JOYPRO_COCKPITS_RENDER_OUT=<folder> keeps the pictures (else a temp folder).
public sealed partial class WebsiteScreenshotTests
{
    [Fact]
    public void CockpitsStickPage_NotesBelowThePage_NotOverIt()
    {
        string? keep = Environment.GetEnvironmentVariable("JOYPRO_COCKPITS_RENDER_OUT");
        string outDir = string.IsNullOrWhiteSpace(keep) ? Path.Combine(Path.GetTempPath(), "joypro-cockpit-notes-" + Guid.NewGuid().ToString("N")) : keep;
        Directory.CreateDirectory(outDir);
        List<string> leaks = [];
        List<string> problems = [];
        int checkedPages = 0;
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                foreach (string theme in Themes)
                {
                    SetTheme(theme);
                    vm.CurrentStage = UiStage.Cockpits;
                    AvaloniaRenderThread.Pump();
                    if (vm.Cockpits.MainPages.Count > 1)
                        vm.Cockpits.SelectedMainPage = vm.Cockpits.MainPages[1];
                    AvaloniaRenderThread.Pump();
                    Shot(vm, "cockpits-stick-notes", theme, 1280, 3400, outDir, leaks, crop: main =>
                    {
                        ManualPageView? page = main.GetVisualDescendants().OfType<ManualPageView>().FirstOrDefault(p => p.IsEffectivelyVisible && p.Page != null);
                        TextBlock? notes = main.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == vm.Cockpits.Notes && vm.Cockpits.HasNotes);
                        if (page == null || notes == null)
                        {
                            problems.Add(theme + ": no stick page or no notes");
                            return null;
                        }
                        checkedPages++;
                        if (page.DrawnSize.Height > page.Bounds.Height + 0.5 || page.DrawnSize.Width > page.Bounds.Width + 0.5)
                            problems.Add(theme + ": the page is drawn " + page.DrawnSize + " in a control of " + page.Bounds.Size);
                        double pageBottom = page.TranslatePoint(new Point(0, page.DrawnSize.Height), main)!.Value.Y;
                        double notesTop = notes.TranslatePoint(new Point(0, 0), main)!.Value.Y;
                        if (notesTop < pageBottom)
                            problems.Add(theme + ": the notes start at " + notesTop + ", the page ends at " + pageBottom + " (page " + page.Bounds + ", desired " + page.DesiredSize +
                                         ", row " + Grid.GetRow(page) + ", parent " + (page.Parent as Control)?.Bounds + ", parent desired " + (page.Parent as Control)?.DesiredSize + ")");
                        return null;
                    });
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.StopHudStreamServer();
                vm.StopGameConfigWatcher();
            }
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(2, checkedPages);
        if (string.IsNullOrWhiteSpace(keep))
            Directory.Delete(outDir, true);
    }
}
