using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Follow input on the Binds page and the live highlight of Visual Assign / the Layout page (docs/ui/live-input.md).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FollowInputTests
{
    private const string Stick = "L-VPC Stick WarBRD {00000000-0000-0000-0000-0000000000F1}";
    private const string Throttle = "Throttle {00000000-0000-0000-0000-0000000000F2}";

    // ---- FollowInputTracker ----

    [Fact]
    public void Tracker_ButtonPress_CountsOnceAndBounceIsIgnored()
    {
        long now = 0;
        FollowInputTracker tracker = new(() => now);
        Assert.Equal("JOY_BTN8", tracker.OnButton(Stick, "JOY_BTN8", true, false));
        Assert.Null(tracker.OnButton(Stick, "JOY_BTN8", false, false));
        now += 30;
        Assert.Null(tracker.OnButton(Stick, "JOY_BTN8", true, false));     // contact bounce
        now += 400;
        Assert.Equal("JOY_BTN8", tracker.OnButton(Stick, "JOY_BTN8", true, false));
        Assert.Null(tracker.OnButton("Keyboard", "LShift", true, false));
    }

    [Fact]
    public void Tracker_RestingButtonsOfTheFirstReport_NeverCount_AShortPressInItDoes()
    {
        long now = 0;
        FollowInputTracker tracker = new(() => now);
        // The first report: the mode switch rests on button 7, the user pressed button 2.
        Assert.Null(tracker.OnButton(Stick, "JOY_BTN7", true, true));
        Assert.Null(tracker.OnButton(Stick, "JOY_BTN2", true, true));
        now += 200;
        Assert.Equal("JOY_BTN2", tracker.OnButton(Stick, "JOY_BTN2", false, false));
        // The resting switch is left after a long time: not a press.
        now += 60_000;
        Assert.Null(tracker.OnButton(Stick, "JOY_BTN7", false, false));
    }

    [Fact]
    public void Tracker_Axis_StepsOnLargeMovesOnly_AndKeepsSteppingWhileMoved()
    {
        long now = 0;
        FollowInputTracker tracker = new(() => now);
        double[] axes = [0.5, 0.5, 0, 0, 0, 0, 0, 0];
        Assert.Null(tracker.OnAxes(Throttle, axes, false));              // first snapshot: the anchor
        Assert.Null(tracker.OnAxes(Throttle, [0.5, 0.5, 1, 0, 0, 0, 0, 0], true)); // first report: where it really is
        Assert.Null(tracker.OnAxes(Throttle, [0.7, 0.55, 1, 0, 0, 0, 0, 0], false));  // 20 %: no
        Assert.Equal("JOY_X", tracker.OnAxes(Throttle, [0.95, 0.55, 1, 0, 0, 0, 0, 0], false));
        now += 100;
        Assert.Null(tracker.OnAxes(Throttle, [0.4, 0.55, 1, 0, 0, 0, 0, 0], false));   // too soon after the last step
        now += 400;
        Assert.Equal("JOY_X", tracker.OnAxes(Throttle, [0.1, 0.55, 1, 0, 0, 0, 0, 0], false));
        now += 400;
        Assert.Null(tracker.OnAxes(Throttle, [0.15, 0.55, 1, 0, 0, 0, 0, 0], false));  // stopped
    }

    // ---- The Binds page ----

    [Fact]
    public async Task Follow_JumpsToTheRelation_AndCyclesUnshiftedFirst()
    {
        (MainWindowViewModel vm, FollowCapture capture) = await CreateBindsPageAsync();
        Assert.True(vm.IsLiveInputMonitoring);
        Assert.True(capture.Monitoring);

        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Gear", vm.SelectedBind?.RelationName);
        Assert.Equal("BTN 8 on L-VPC Stick WarBRD: Gear (1/3)", vm.FollowInputToast);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));

        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Hook", vm.SelectedBind?.RelationName);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));

        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Lights", vm.SelectedBind?.RelationName);
        Assert.Contains("(with modifier) (3/3)", vm.FollowInputToast);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));

        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Gear", vm.SelectedBind?.RelationName);

        // Another input starts at its first relation.
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        Assert.Equal("BTN 1 on L-VPC Stick WarBRD: Fire", vm.FollowInputToast);

        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN30", true));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        Assert.Contains("nothing bound", vm.FollowInputToast);
    }

    [Fact]
    public async Task Follow_Axis_MovedALongWay_JumpsThroughItsRelations()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.05, 0, 0, 0, 0, 0], FirstReport: true));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.2, 0, 0, 0, 0, 0]));
        Assert.NotEqual("Thrust", vm.SelectedBind?.RelationName);
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.9, 0, 0, 0, 0, 0]));
        Assert.Equal("Thrust", vm.SelectedBind?.RelationName);
        await Task.Delay((int)FollowInputTracker.AxisStepGapMs + 50);
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.1, 0, 0, 0, 0, 0]));
        Assert.Equal("Zoom", vm.SelectedBind?.RelationName);
    }

    [Fact]
    public async Task Follow_DoesNotJump_WhileRecordingTypingOrSwitchedOff()
    {
        (MainWindowViewModel vm, FollowCapture capture) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Fire");

        vm.IsTextInputFocused = true;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        vm.IsTextInputFocused = false;

        vm.IsRecordingInput = true;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        vm.IsRecordingInput = false;

        vm.FollowInputOnBinds = false;
        Assert.False(vm.IsLiveInputMonitoring);
        Assert.True(capture.LastToken.IsCancellationRequested);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);

        vm.FollowInputOnBinds = true;
        Assert.True(vm.IsLiveInputMonitoring);
        vm.CurrentStage = UiStage.Relations;
        Assert.True(vm.IsLiveInputMonitoring);
        vm.CurrentStage = UiStage.Export;
        Assert.False(vm.IsLiveInputMonitoring);
    }

    // Tester report on 2.4.2: "JP always jumps to the button pressed in the Move dialogue box". While the Move panel is
    // open (pressing its new button, or any button), neither page's selection nor the open relation changes.
    [Fact]
    public async Task Follow_NeverJumps_WhileTheMovePanelIsOpen_OnBindsAndRelations()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Fire");
        int jumps = 0;
        vm.FollowInputJumped += (_, _) => jumps++;
        vm.IsMoveActionOpen = true;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);

        vm.CurrentStage = UiStage.Relations;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Fire");
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.9, 0, 0, 0, 0, 0]));
        Assert.Equal("Fire", vm.SelectedRelation?.Name);
        Assert.Equal(0, jumps);

        // Closed again: following works as before.
        vm.IsMoveActionOpen = false;
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Gear", vm.SelectedRelation?.Name);
    }

    // ---- The Relations page (same switch, same rules) ----

    [Fact]
    public async Task Follow_OnTheRelationsPage_SelectsTheCard_AndCyclesUnshiftedFirst()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.CurrentStage = UiStage.Relations;
        Assert.True(vm.IsLiveInputMonitoring);
        int jumps = 0;
        vm.FollowInputJumped += (_, _) => jumps++;

        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Gear", vm.SelectedRelation?.Name);
        Assert.Equal("BTN 8 on L-VPC Stick WarBRD: Gear (1/3)", vm.FollowInputToast);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Hook", vm.SelectedRelation?.Name);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Lights", vm.SelectedRelation?.Name);
        Assert.Contains("(with modifier) (3/3)", vm.FollowInputToast);
        Assert.Equal(3, jumps);
        // The Binds page's selection is not touched by the Relations page.
        Assert.Null(vm.SelectedBind);

        // An axis moved a long way.
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.9, 0, 0, 0, 0, 0]));
        Assert.Equal("Thrust", vm.SelectedRelation?.Name);

        // Hidden by the search: the search is cleared.
        vm.RelationSearchText = "Fire";
        Assert.DoesNotContain(vm.Relations, r => r.Name == "Gear");
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", false));
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        Assert.Equal("Fire", vm.SelectedRelation?.Name);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal(string.Empty, vm.RelationSearchText);
        Assert.Equal("Gear", vm.SelectedRelation?.Name);
    }

    [Fact]
    public async Task Follow_OnTheRelationsPage_NotWhileTypingRecordingOrSwitchedOff()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.CurrentStage = UiStage.Relations;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Fire");

        vm.IsTextInputFocused = true;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedRelation?.Name);
        vm.IsTextInputFocused = false;

        vm.IsRecordingInput = true;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedRelation?.Name);
        vm.IsRecordingInput = false;

        vm.FollowInputOnBinds = false;
        Assert.False(vm.IsLiveInputMonitoring);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Fire", vm.SelectedRelation?.Name);
    }

    // A jump moves the list under a mouse that did not move: the window holds tooltips back until the mouse really
    // moves (screen path >= ReleasePath); synthetic moves at the same place, jitter, clicks and the wheel do not count.
    [Fact]
    public void Follow_Jump_HoldsTooltipsBack_UntilTheMouseReallyMoves()
    {
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _) = AvaloniaRenderThread.Wait(CreateBindsPageAsync());
            vm.CurrentStage = UiStage.Relations;
            Views.MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
            Avalonia.Controls.Control inner = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main)
                .OfType<Avalonia.Controls.ListBox>().First(l => l.Name == "RelationsListBox");
            Views.TooltipHold hold = main.TooltipHold;
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            // Through the real handler (window position to screen position).
            void Move(Avalonia.Point at) => RaiseMove(main, main, pointer, at);
            Move(new Avalonia.Point(400, 300));
            Assert.True(Avalonia.Controls.ToolTip.GetServiceEnabled(inner));
            Assert.False(Views.TooltipHold.KeepsBack(inner));

            vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
            Assert.Equal("Gear", vm.SelectedRelation?.Name);
            Assert.True(hold.IsHolding);
            Assert.False(Avalonia.Controls.ToolTip.GetServiceEnabled(inner));
            Assert.True(Views.TooltipHold.KeepsBack(inner));

            // The layout raising pointer moves under the still mouse (the real handler, same screen place).
            for (int i = 0; i < 50; i++)
                Move(new Avalonia.Point(400, 300));
            Assert.True(hold.IsHolding);
            // Clicks and the wheel are no reason for a tooltip.
            inner.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(inner, pointer, main, new Avalonia.Point(400, 300), 0,
                new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                Avalonia.Input.KeyModifiers.None));
            inner.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(inner, pointer, main, new Avalonia.Point(400, 300), 0,
                Avalonia.Input.PointerPointProperties.None, Avalonia.Input.KeyModifiers.None, new Avalonia.Vector(0, -1)));
            Assert.True(hold.IsHolding);

            // Jitter: a resting hand moves the mouse a pixel or two to and fro for a long time.
            for (int i = 0; i < 200; i++)
                Move(new Avalonia.Point(400 + (i % 2) * 2, 300 + (i % 3)));
            Assert.True(hold.IsHolding);
            // A small move is not enough either.
            Move(new Avalonia.Point(430, 300));
            Assert.True(hold.IsHolding);

            // A noticeable move (60+ px on the screen): tooltips come back, with their normal show delay.
            for (int x = 430; x <= 500; x += 5)
                Move(new Avalonia.Point(x, 300));
            Assert.False(hold.IsHolding);
            Assert.True(Avalonia.Controls.ToolTip.GetServiceEnabled(inner));
            Assert.False(Views.TooltipHold.KeepsBack(inner));

            // Every new press arms it again, also one that does not move the selection.
            vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
            System.Threading.Thread.Sleep((int)FollowInputTracker.ButtonDebounceMs + 30);
            vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
            Assert.True(hold.IsHolding);
            for (int x = 500; x <= 580; x += 5)
                Move(new Avalonia.Point(x, 300));
            Assert.False(hold.IsHolding);
            Assert.Equal("Fire", vm.SelectedRelation?.Name);
            vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", false));
            System.Threading.Thread.Sleep((int)FollowInputTracker.ButtonDebounceMs + 30);
            vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
            Assert.Equal("Fire", vm.SelectedRelation?.Name);
            Assert.True(hold.IsHolding);
            for (int x = 580; x >= 500; x -= 5)
                Move(new Avalonia.Point(x, 300));
            Assert.False(hold.IsHolding);

            // Each axis jump arms it again, also a large move too soon after the last step (no jump).
            vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
            vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.9, 0, 0, 0, 0, 0]));
            Assert.True(hold.IsHolding);
            for (int x = 580; x <= 660; x += 5)
                Move(new Avalonia.Point(x, 300));
            Assert.False(hold.IsHolding);
            vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.2, 0, 0, 0, 0, 0]));
            Assert.True(hold.IsHolding);
        });
    }

    // An inactive window (the game in front) never shows a tooltip; back in front, the mouse has to move first.
    [Fact]
    public void Tooltips_NeverInAnInactiveWindow()
    {
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _) = AvaloniaRenderThread.Wait(CreateBindsPageAsync());
            vm.FollowInputOnBinds = false;
            Views.MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
            Views.TooltipHold hold = main.TooltipHold;
            hold.PointerAtScreen(new Avalonia.Point(200, 300));
            Assert.True(Avalonia.Controls.ToolTip.GetServiceEnabled(main));

            hold.SetActive(false);
            Assert.True(hold.IsSuppressed);
            Assert.False(Avalonia.Controls.ToolTip.GetServiceEnabled(main));
            for (int x = 200; x <= 400; x += 5)
                hold.PointerAtScreen(new Avalonia.Point(x, 300));
            Assert.True(hold.IsSuppressed);

            hold.SetActive(true);
            Assert.True(hold.IsSuppressed);
            for (int x = 400; x <= 480; x += 5)
                hold.PointerAtScreen(new Avalonia.Point(x, 300));
            Assert.False(hold.IsSuppressed);
            Assert.True(Avalonia.Controls.ToolTip.GetServiceEnabled(main));
        });
    }

    private static void RaiseMove(Avalonia.Controls.Window main, Avalonia.Controls.Control source, Avalonia.Input.IPointer pointer, Avalonia.Point at) =>
        source.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, source, pointer, main, at, 0,
            Avalonia.Input.PointerPointProperties.None, Avalonia.Input.KeyModifiers.None));

    // ---- Analog keyboards (Wooting & co.): typing moves their axes ----

    private const string Wooting = "Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000F9}";

    [Fact]
    public void KeyboardLike_AnalogKeyboardsAndCatalogKeyboards_SticksNot()
    {
        Func<JoyPro.DeviceReference.DeviceCatalog?> previous = KeyboardLikeDevices.CatalogSource;
        try
        {
            JoyPro.DeviceReference.DeviceCatalog catalog = new();
            catalog.Devices.Add(new JoyPro.DeviceReference.RefDevice { Id = "acme-k1", Name = "Acme K1", Kind = "keyboard", Names = ["ACME HID Board"] });
            KeyboardLikeDevices.ClearCache();
            KeyboardLikeDevices.CatalogSource = () => catalog;
            Assert.True(KeyboardLikeDevices.IsKeyboardLike(Wooting));
            Assert.True(KeyboardLikeDevices.IsKeyboardLike("Razer Huntsman V3 Pro {GUID}"));
            Assert.True(KeyboardLikeDevices.IsKeyboardLike("SteelSeries Apex Pro TKL"));
            Assert.True(KeyboardLikeDevices.IsKeyboardLike("Keychron Q1 HE"));
            Assert.True(KeyboardLikeDevices.IsKeyboardLike("Some Gaming Keyboard"));
            Assert.True(KeyboardLikeDevices.IsKeyboardLike("ACME HID Board {00000000-0000-0000-0000-000000000001}"));
            Assert.False(KeyboardLikeDevices.IsKeyboardLike(Stick));
            Assert.False(KeyboardLikeDevices.IsKeyboardLike(Throttle));
            Assert.False(KeyboardLikeDevices.IsKeyboardLike("Keyboard"));
        }
        finally
        {
            KeyboardLikeDevices.CatalogSource = previous;
            KeyboardLikeDevices.ClearCache();
        }
    }

    [Fact]
    public async Task Follow_TypingOnAnAnalogKeyboard_DoesNotJump_ARealAxisStillDoes_OptInCounts()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        int activity = 0;
        vm.FollowInputActivity += (_, _) => activity++;
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Fire");

        // Typing: the keys' analog axes jump from 0 to full travel and back.
        vm.OnLiveAxes(new JoystickAxisSnapshot(Wooting, [0, 0, 0, 0, 0, 0, 0, 0]));
        for (int i = 0; i < 6; i++)
        {
            vm.OnLiveAxes(new JoystickAxisSnapshot(Wooting, [i % 2 == 0 ? 1 : 0, 0, 1, 0, 0, 0, 0, 0]));
            await Task.Delay((int)FollowInputTracker.AxisStepGapMs + 20);
        }
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        Assert.Equal(string.Empty, vm.FollowInputToast);
        Assert.Equal(0, activity);

        // The throttle still follows.
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Throttle, [0.5, 0.5, 0.9, 0, 0, 0, 0, 0]));
        Assert.Equal("Thrust", vm.SelectedBind?.RelationName);

        // The keyboard's keys (buttons) still count: keyboard binds jump.
        vm.OnLiveInput(new JoystickInputEvent(Wooting, "JOY_BTN5", true));
        Assert.Contains("nothing bound", vm.FollowInputToast);

        // Opted in (Settings > Devices): its axes count.
        vm.SetDeviceAxesInput(Wooting, true);
        Assert.True(vm.UsesAxesOf(Wooting));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Wooting, [0, 0, 0, 0, 0, 0, 0, 0]));
        vm.OnLiveAxes(new JoystickAxisSnapshot(Wooting, [0.9, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Contains("on Wooting", vm.FollowInputToast);
    }

    [Fact]
    public async Task Follow_NotWhileAnotherWindowIsInFront_OrATextBoxHoldsTheFocus_NoteCanBeHidden()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Fire");

        vm.IsMainWindowActive = false;
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        vm.IsMainWindowActive = true;

        // The focus flag lags behind (no focus event yet): the live check still holds.
        bool typing = true;
        vm.TextInputFocusProbe = () => typing;
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        Assert.Equal("Fire", vm.SelectedBind?.RelationName);
        typing = false;

        vm.ShowFollowInputNote = false;
        await Task.Delay((int)FollowInputTracker.ButtonDebounceMs + 30);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.Equal("Gear", vm.SelectedBind?.RelationName);
        Assert.Equal(string.Empty, vm.FollowInputToast);
        Assert.False(vm.HasFollowInputToast);
    }

    [Fact]
    public async Task Record_LeavesTheAxesOfAnAnalogKeyboardOut_UnlessOptedIn()
    {
        (MainWindowViewModel vm, FollowCapture capture) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Thrust");
        await vm.RecordNextInputCommand.ExecuteAsync(null);
        Func<string, bool>? ignore = capture.LastOptions?.IgnoreAxesOf;
        Assert.NotNull(ignore);
        Assert.True(ignore!(Wooting));
        Assert.False(ignore(Throttle));
        Assert.False(ignore(Stick));

        vm.SetDeviceAxesInput(Wooting, true);
        await vm.RecordNextInputCommand.ExecuteAsync(null);
        Assert.False(capture.LastOptions!.IgnoreAxesOf!(Wooting));
    }

    // A key pressed in the window (also one that makes follow input jump) and every jump arm the tooltip hold; no
    // control opens its tooltip at once because another one was open a moment ago.
    [Fact]
    public void Tooltips_KeyPressAndEveryJump_ArmTheHold_NoInstantTooltips()
    {
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _) = AvaloniaRenderThread.Wait(CreateBindsPageAsync());
            vm.CurrentStage = UiStage.Relations;
            Views.MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
            Views.TooltipHold hold = main.TooltipHold;
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            void Move(Avalonia.Point at) => RaiseMove(main, main, pointer, at);
            Move(new Avalonia.Point(400, 300));
            Assert.False(hold.IsHolding);

            // Typing (no follow input involved): the pointer rests over the list, no tooltip.
            main.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A, Source = main });
            Assert.True(hold.IsHolding);
            for (int i = 0; i < 20; i++)
                Move(new Avalonia.Point(400, 300));
            Assert.True(hold.IsHolding);
            for (int x = 400; x <= 480; x += 5)
                Move(new Avalonia.Point(x, 300));
            Assert.False(hold.IsHolding);

            // A jump by a keyboard's key (its game-controller button) arms it as well.
            vm.OnLiveInput(new JoystickInputEvent(Wooting, "JOY_BTN5", true));
            Assert.True(hold.IsHolding);
            Assert.False(Avalonia.Controls.ToolTip.GetServiceEnabled(main));

            // No instant tooltip along a list: every control with a tooltip waits for its show delay.
            Avalonia.Controls.Control tipped = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main)
                .OfType<Avalonia.Controls.Control>().First(c => Avalonia.Controls.ToolTip.GetTip(c) != null);
            Assert.Equal(-1, Avalonia.Controls.ToolTip.GetBetweenShowDelay(tipped));
            Avalonia.Controls.Button later = new();
            Avalonia.Controls.ToolTip.SetTip(later, "later");
            Assert.Equal(-1, Avalonia.Controls.ToolTip.GetBetweenShowDelay(later));
        });
    }

    [Fact]
    public async Task Follow_ARestingButtonOfTheFirstReport_DoesNotJump()
    {
        (MainWindowViewModel vm, _) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Thrust");
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true, FirstReport: true));
        Assert.Equal("Thrust", vm.SelectedBind?.RelationName);
    }

    // ---- Visual Assign / Layout page: the shown device's inputs light up ----

    [Fact]
    public void LiveInputState_ButtonsAndAxes()
    {
        long now = 0;
        LiveInputState state = new(() => now);
        int changes = 0;
        state.Changed += (_, _) => changes++;
        Assert.True(state.SetButton("JOY_BTN3", true));
        Assert.False(state.SetButton("JOY_BTN3", true));
        Assert.True(state.IsActive("joy_btn3"));
        Assert.True(state.SetButton("JOY_BTN3", false));
        Assert.False(state.AnyActive);

        state.SetAxes([0.5, 0.5, 0, 0, 0, 0, 0, 0]);
        Assert.False(state.IsActive("JOY_X"));                       // the first values are no move
        Assert.Equal(["JOY_X"], state.SetAxes([0.6, 0.5, 0, 0, 0, 0, 0, 0]));
        Assert.True(state.IsActive("JOY_X"));
        Assert.Equal(0.6, state.AxisValue("JOY_X"), 3);
        Assert.Empty(state.SetAxes([0.605, 0.5, 0, 0, 0, 0, 0, 0]));  // noise
        now += LiveInputState.AxisActiveMs + 10;
        Assert.False(state.IsActive("JOY_X"));
        Assert.Equal(["JOY_X"], state.ExpiredAxes(new HashSet<string> { "JOY_X" }));
        Assert.True(changes >= 4);
    }

    [Fact]
    public async Task VisualAssign_PressedInputLightsUpItsRowAndStopsWithThePage()
    {
        (MainWindowViewModel vm, FollowCapture capture) = await CreateBindsPageAsync();
        vm.FollowInputOnBinds = false;
        Assert.False(vm.IsLiveInputMonitoring);
        vm.VisualAssignDevice = Stick;
        vm.OpenVisualAssignMode = true;
        Assert.True(vm.IsLiveInputMonitoring);
        Assert.True(vm.IsWorkspaceDevice(Stick));
        Assert.False(vm.IsWorkspaceDevice(Throttle));

        LayoutEditorMarkerItem marker = new() { Token = "JOY_BTN8" };
        LayoutEditorConnectorItem line = new() { TargetToken = "JOY_BTN8" };
        vm.LayoutEditorMarkers.Add(marker);
        vm.LayoutEditorConnectors.Add(line);

        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        Assert.True(vm.WorkspaceLiveInput.IsPressed("JOY_BTN8"));
        Assert.True(marker.IsLive);
        Assert.True(line.IsLive);
        Assert.Equal("#F59E0B", line.DisplayStroke);
        // Visual Assign is open: the list does not jump (the user binds on the picture).
        Assert.NotEqual("Gear", vm.SelectedBind?.RelationName);

        vm.OnLiveInput(new JoystickInputEvent(Throttle, "JOY_BTN8", false));   // another device
        Assert.True(marker.IsLive);
        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", false));
        Assert.False(marker.IsLive);
        Assert.False(line.IsLive);

        vm.OnLiveInput(new JoystickInputEvent(Stick, "JOY_BTN8", true));
        vm.OpenVisualAssignMode = false;
        Assert.False(vm.IsLiveInputMonitoring);
        Assert.False(marker.IsLive);
        Assert.Empty(vm.WorkspaceLiveInput.Pressed);
    }

    [Fact]
    public void Settings_FollowInputAndRecordRelease_ArePersisted()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "follow-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            MainWindowViewModel first = new(new FollowEngine(), new FollowCapture(), loadPersistedUiSettings: true);
            Assert.True(first.FollowInputOnBinds);
            Assert.False(first.RecordReleaseOfHeldButtons);
            Assert.False(first.ShowFollowInputNote);
            Assert.False(first.UsesAxesOf(Wooting));
            first.FollowInputOnBinds = false;
            first.RecordReleaseOfHeldButtons = true;
            first.ShowFollowInputNote = true;
            first.SetDeviceAxesInput(Wooting, true);

            MainWindowViewModel second = new(new FollowEngine(), new FollowCapture(), loadPersistedUiSettings: true);
            Assert.False(second.FollowInputOnBinds);
            Assert.True(second.RecordReleaseOfHeldButtons);
            Assert.True(second.ShowFollowInputNote);
            Assert.True(second.UsesAxesOf(Wooting));
            Assert.False(second.UsesAxesOf("Keychron Q1 HE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Record_PassesTheReleaseSettingToTheCapture()
    {
        (MainWindowViewModel vm, FollowCapture capture) = await CreateBindsPageAsync();
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Fire");
        vm.RecordReleaseOfHeldButtons = true;
        await vm.RecordNextInputCommand.ExecuteAsync(null);
        Assert.True(capture.LastOptions?.RecordReleaseOfHeldButtons);
    }

    private static async Task<(MainWindowViewModel Vm, FollowCapture Capture)> CreateBindsPageAsync()
    {
        FollowCapture capture = new();
        MainWindowViewModel vm = new(new FollowEngine(), capture, loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "follow.pr0file"),
            // Off by default; these tests check the note's text.
            ShowFollowInputNote = true
        };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Binds;
        return (vm, capture);
    }

    internal sealed class FollowCapture : IJoystickCaptureService
    {
        public string LastDiagnostics => string.Empty;
        public bool Monitoring { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public JoystickCaptureService.CaptureOptions? LastOptions { get; private set; }

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.FromResult<JoystickCaptureService.CaptureResult?>(null);
        }

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken)
        {
            Monitoring = true;
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    // Relations and binds: BTN 8 of the stick does Gear and Hook, with a modifier Lights; the throttle's Z axis does
    // Thrust and Zoom.
    private sealed class FollowEngine : IEngineCliClient
    {
        private static readonly string[] Relations = ["Fire", "Gear", "Hook", "Lights", "Thrust", "Zoom"];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = string.Empty;
            List<string> args = arguments.ToList();
            bool text = args.Contains("--text");
            if (Has(args, "relations", "list"))
            {
                output = true
                    ? "Relations:" + Environment.NewLine + string.Join(Environment.NewLine, Relations.Select(r =>
                        r + " | type=" + (r is "Thrust" or "Zoom" ? "axis" : "button") + " | bind=bound | mods=0 | games=DCS | planes=1 | groups=Test"))
                    : string.Empty;
            }
            else if (Has(args, "binds", "list"))
            {
                output = string.Join(Environment.NewLine,
                    "Binds:",
                    "Fire | type=button | device=" + Stick + " | input=JOY_BTN1 | modifiers(0)=<none>",
                    "Lights | type=button | device=" + Stick + " | input=JOY_BTN8 | modifiers(1)=Shift",
                    "Gear | type=button | device=" + Stick + " | input=JOY_BTN8 | modifiers(0)=<none>",
                    "Hook | type=button | device=" + Stick + " | input=JOY_BTN8 | modifiers(0)=<none>",
                    "Thrust | type=axis | device=" + Throttle + " | input=JOY_Z | modifiers(0)=<none>",
                    "Zoom | type=axis | device=" + Throttle + " | input=JOY_Z | modifiers(0)=<none>");
            }
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static bool Has(List<string> args, string a, string b)
        {
            int i = args.IndexOf(a);
            return i >= 0 && i + 1 < args.Count && args[i + 1] == b;
        }
    }
}
