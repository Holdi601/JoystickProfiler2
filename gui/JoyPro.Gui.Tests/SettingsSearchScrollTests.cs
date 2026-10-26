using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// A search that hides items starts its result at the top, in a hidden real window (HiddenWindow: Avalonia's own layout
// and scroll passes, never on the desktop), after the page was scrolled down first:
//   - Settings: the first matching section stands right under the tabs (2.4.2: the sections panel returned a size
//     smaller than its slot, so a short result was centered in the page, ~220 px under the tabs at 2267x1250); clearing
//     the search goes back to where the page was.
//   - Relations: the first matching relation is the list's first row (the 2.4.0 bug of the same kind).
// With JOYPRO_GUI_PARITY_PREVIEW set the states are saved as PNGs.
[SupportedOSPlatform("windows")]
public sealed class SettingsSearchScrollTests
{
    private const double Tolerance = 16;

    [Fact]
    public void SettingsSearch_StartsAtTheTop_ClearingGoesBack()
    {
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
            {
                MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
                vm.CurrentStage = UiStage.Settings;
                using HiddenWindow host = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                host.Settle();
                ScrollViewer scroller = host.Page.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "SettingsScrollViewer");
                vm.ExpandAllSettingsCommand.Execute(null);
                host.Settle();
                scroller.Offset = new Vector(0, 3000);
                host.Settle();
                double scrolled = scroller.Offset.Y;
                Assert.True(scrolled > 1000, "the page did not scroll: " + State(scroller));

                foreach (string search in new[] { "Include my open profile", "tray", "Keep background logs" })
                {
                    vm.SettingsSearchText = search;
                    host.Settle();
                    host.Snap("settings-search-" + width + "-" + search.Replace(' ', '-') + ".png");
                    Views.Controls.SettingsSection first = ((Views.Controls.SettingsSectionsPanel)scroller.Content!).OrderedChildren()
                        .OfType<Views.Controls.SettingsSection>().First(s => s.IsVisible);
                    double gap = first.TranslatePoint(new Point(0, 0), scroller)!.Value.Y;
                    Assert.True(gap >= 0 && gap <= Tolerance, width + "x" + height + ", search '" + search + "': the first section is " + gap.ToString("0") + " px under the tabs. " + State(scroller));
                }

                vm.SettingsSearchText = string.Empty;
                host.Settle();
                Assert.True(Math.Abs(scroller.Offset.Y - scrolled) < 1, width + ": clearing the search did not go back to " + scrolled.ToString("0") + ". " + State(scroller));
            }
        });
    }

    [Fact]
    public void RelationsSearch_ShowsTheFirstMatchAtTheTop()
    {
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
            {
                LocalAiFakeEngine engine = new() { RelationCount = 80 };
                MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                    ProfilePath = Path.Combine(Path.GetTempPath(), "relations-search-scroll.pr0file")
                };
                vm.CurrentStage = UiStage.Relations;
                using HiddenWindow host = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                host.Settle();
                ListBox list = host.Window.FindControl<ListBox>("RelationsListBox")!;
                ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Offset = new Vector(0, 2500);
                host.Settle();
                Assert.True(scroller.Offset.Y > 500, "the list did not scroll: " + State(scroller));

                vm.RelationSearchText = "Relation 05";
                host.Settle();
                host.Snap("relations-search-" + width + ".png");
                Control firstRow = list.GetRealizedContainers().OrderBy(c => c.Bounds.Top).First();
                double gap = firstRow.TranslatePoint(new Point(0, 0), scroller)!.Value.Y;
                Assert.Equal("Relation 050", (firstRow.DataContext as RelationListItem)?.Name);
                Assert.True(gap >= -1 && gap <= Tolerance, width + ": the first match is " + gap.ToString("0") + " px under the list's top. " + State(scroller));
            }
        });
    }

    private static string State(ScrollViewer scroller) =>
        "offset " + scroller.Offset.Y.ToString("0") + ", extent " + scroller.Extent.Height.ToString("0") + ", viewport " + scroller.Viewport.Height.ToString("0") +
        ", content at " + ((Control?)scroller.Content)?.Bounds.Y.ToString("0");
}
