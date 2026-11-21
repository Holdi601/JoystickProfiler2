using System;
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

// The watcher dialog when taking over would remove many binds (a launcher or an update rewrote the game's files): the
// warning is shown, "Keep my JoyPro binds" is the default button (Enter) and comes first, "Take over" is only offered
// as "Take over anyway". With a few removals the dialog is the usual one. Rendered in a hidden root.
[SupportedOSPlatform("windows")]
public sealed class GameSyncMassRemovalRenderTests
{
    [Fact]
    public void ManyRemovals_ShowTheWarning_AndKeepIsTheDefaultButton()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 900));
            vm.GameSyncChanges.Add(new GameSyncChangeItem("r1", "removed", "FalconBMS: 'Pickle' no longer on the stick BTN 1", ["Pickle"], "Pickle", string.Empty));
            vm.GameSyncTitle = "Falcon BMS: 207 changes made in the game";
            vm.GameSyncMassRemoval = true;
            vm.GameSyncMassRemovalWarning = "Taking over would remove binds from JoyPro: 207 of 222 for Falcon BMS.";
            vm.HasGameSyncChanges = true;
            main.Settle();
            main.Snap("game-sync-mass-removal.png");

            Button keep = VisibleButton(main, "Keep my JoyPro binds");
            Assert.True(keep.IsDefault, "Keep my JoyPro binds must be the default button");
            Assert.Same(vm.IgnoreGameChangesCommand, keep.Command);
            Assert.Same(vm.TakeOverGameChangesCommand, VisibleButton(main, "Take over anyway").Command);
            Assert.DoesNotContain(main.Page.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Take over"));
            Assert.Contains(main.Page.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Contains("207 of 222", StringComparison.Ordinal));

            vm.GameSyncMassRemoval = false;
            vm.GameSyncMassRemovalWarning = string.Empty;
            main.Settle();
            Assert.False(VisibleButton(main, "Take over").IsDefault);
            Assert.DoesNotContain(main.Page.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Keep my JoyPro binds"));
        });
    }

    private static Button VisibleButton(HiddenWindow main, string label) =>
        main.Page.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, label));
}
