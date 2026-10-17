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
        Assert.False(vm.IsLiveInputMonitoring);
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
            first.FollowInputOnBinds = false;
            first.RecordReleaseOfHeldButtons = true;

            MainWindowViewModel second = new(new FollowEngine(), new FollowCapture(), loadPersistedUiSettings: true);
            Assert.False(second.FollowInputOnBinds);
            Assert.True(second.RecordReleaseOfHeldButtons);
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
            ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "follow.pr0file")
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
