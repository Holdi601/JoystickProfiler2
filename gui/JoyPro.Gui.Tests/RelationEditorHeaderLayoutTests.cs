using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The relation editor's header (name, chips, "Its actions" with Suggest missing / Expand with AI / Duplicate / delete,
// and the search row of Add actions on the right) at the window's smallest width and wider: no control is drawn over
// another (a user saw the right column's search box over the delete button in a narrow window). Sizes are logical
// pixels: 1200 is the window's MinWidth, 1280x720 is a 1920x1080 screen at 150 % scaling, 2267x1250 the user's window.
// The page is laid out in a hidden root, never shown. JOYPRO_GUI_PARITY_PREVIEW names a folder for the pictures.
[SupportedOSPlatform("windows")]
public sealed class RelationEditorHeaderLayoutTests
{
    private static readonly (int Width, int Height)[] Sizes = [(1200, 760), (1280, 720), (1280, 800), (1920, 1080), (2267, 1250)];

    [Fact]
    public void Header_AtEveryWidth_NoControlOverlapsAnother()
    {
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int width, int height) in Sizes)
            {
                BuilderFakeEngine fake = new();
                MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                    ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "relation-header.pr0file")
                };
                vm.CurrentStage = UiStage.Relations;
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Flare");
                AvaloniaRenderThread.Wait(RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBusy));
                main.Settle();
                main.Snap("relation-header-" + width + "x" + height + ".png");
                problems.AddRange(Overlaps(main.Window).Select(p => width + "x" + height + ": " + p));
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // The same with the Catalog tab open (report: at 1280 the delete button lay under the catalog's game picker).
    [Fact]
    public void Header_WithTheCatalogOpen_AtEveryWidth_NoControlOverlapsAnother()
    {
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int width, int height) in Sizes)
            {
                BuilderFakeEngine fake = new();
                MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                    ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "relation-header.pr0file")
                };
                vm.CurrentStage = UiStage.Relations;
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Flare");
                AvaloniaRenderThread.Wait(RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBusy));
                AvaloniaRenderThread.Wait(vm.OpenCatalogCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(RelationBuilderTests.WaitForAsync(() => vm.CatalogCategories.Count > 0 && !vm.IsCatalogLoading));
                main.Settle();
                main.Snap("relation-header-catalog-" + width + "x" + height + ".png");
                if (!vm.IsBuilderCatalogPanel)
                    problems.Add(width + "x" + height + ": the catalog did not open");
                problems.AddRange(Overlaps(main.Window).Select(p => width + "x" + height + " (catalog): " + p));
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // Controls of the editor's header rows (above the action list and the search results) whose boxes intersect.
    internal static List<string> Overlaps(Window window)
    {
        Control row = window.FindControl<Control>("RelationNameEditBox")!;
        Control search = window.FindControl<Control>("RelationNodeSearchTextBox")!;
        Visual editor = row.GetVisualAncestors().First(a => search.GetVisualAncestors().Contains(a));
        double bottom = new[] { window.FindControl<Control>("RelationNodeList"), window.FindControl<Control>("BuilderSearchScroll") }
            .Where(c => c != null && c.IsEffectivelyVisible && c.Bounds.Height > 0)
            .Select(c => c!.TranslatePoint(new Point(0, 0), editor)?.Y ?? double.MaxValue)
            .DefaultIfEmpty(double.MaxValue).Min();
        List<(Control Control, Rect Box)> controls = editor.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or TextBox or ToggleButton or ComboBox or TextBlock && c.IsEffectivelyVisible && c.Bounds.Width > 0.5 && c.Bounds.Height > 0.5)
            .Select(c => (c, new Rect(c.TranslatePoint(new Point(0, 0), editor) ?? default, c.Bounds.Size)))
            .Where(c => c.Item2.Top < bottom - 1)
            .ToList();
        // A text inside a button (or a box) is part of it.
        controls = controls.Where(c => !c.Control.GetVisualAncestors().OfType<Control>().Any(a => a is Button or TextBox or ToggleButton or ComboBox)).ToList();
        List<string> problems = [];
        for (int i = 0; i < controls.Count; i++)
        {
            for (int j = i + 1; j < controls.Count; j++)
            {
                Rect overlap = controls[i].Box.Intersect(controls[j].Box);
                if (overlap.Width > 1 && overlap.Height > 1)
                    problems.Add(Describe(controls[i].Control) + " " + controls[i].Box + " overlaps " + Describe(controls[j].Control) + " " + controls[j].Box);
            }
        }
        return problems;
    }

    private static string Describe(Control control) =>
        control.GetType().Name + (control.Name is { Length: > 0 } name ? "#" + name : string.Empty) +
        (control is ContentControl { Content: string text } ? " '" + text + "'" : control is TextBlock block ? " '" + block.Text + "'" : string.Empty) +
        (control is Button { Tag: not null } && control is ContentControl { Content: null } ? " (icon)" : string.Empty);
}
