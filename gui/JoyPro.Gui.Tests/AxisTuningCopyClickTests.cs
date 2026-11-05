using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.6.2 tester report "Copy to another axis doesn't work": the Copy button's click closed its flyout before the
// button ran its command; a closed flyout's content has no DataContext, so the command was gone and nothing was sent.
// The other tests call the command directly, so this one presses the real button in the open flyout.
[SupportedOSPlatform("windows")]
public sealed class AxisTuningCopyClickTests
{
    private static void Press(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    [Fact]
    public void CopyButton_InTheOpenFlyout_SendsTheCopy_AndClosesIt()
    {
        AvaloniaRenderThread.Run(() =>
        {
            AxisTuningFakeEngine fake = new();
            fake.OtherAxes.Add(("Roll", "JOY_X"));
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake));
            vm.CurrentStage = UiStage.Binds;
            AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50"));
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 2400));
            main.Settle();

            Button open = main.Page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AxisTuningCopyButton");
            Assert.True(open.IsEffectivelyEnabled);
            Press(open);
            main.Settle();
            Flyout flyout = Assert.IsType<Flyout>(open.Flyout);
            Assert.True(flyout.IsOpen, "the flyout did not open");
            Assert.Equal("Roll", vm.AxisTuningCopyTarget?.Relation);

            StackPanel panel = Assert.IsType<StackPanel>(flyout.Content);
            Button copy = panel.Children.OfType<Button>().Single(b => b.Name == "AxisTuningCopyConfirm");
            Assert.True(copy.IsEffectivelyEnabled);

            fake.Calls.Clear();
            Press(copy);
            main.Settle();
            Dispatcher.UIThread.RunJobs();
            IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set", "Roll"));
            Assert.True(AxisTuningTests.Seq(set, "binds", "tuning", "set", "Roll", "--scope", "plane:DCS:F-16C_50"));
            Assert.False(flyout.IsOpen, "the flyout stayed open");
            Assert.Contains("'Roll'", vm.StatusText);
        });
    }
}
