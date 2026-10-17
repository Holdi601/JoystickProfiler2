using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;
using static JoyPro.Gui.Tests.AxisTuningTests;

namespace JoyPro.Gui.Tests;

// Direction test, centring spring, per-device inverts, VPforce hint and the editor's trim / button capture. Every
// force goes to fakes: no test starts JoyPro.Ffb or touches a device.
[SupportedOSPlatform("windows")]
public sealed class FfbTrimDirectionTests
{
    private const string RhinoGuid = "{AAAAAAAA-1111-2222-3333-444444444444}";

    // ------------------------------------------------------------------ page settings

    [Fact]
    public async Task VpforceHint_ShowsOnlyForVpforceSticks()
    {
        (MainWindowViewModel vm, _, _) = await FfbTelemetryTests.CreateLoadedVmAsync();
        await vm.RefreshFfbTelemetryDevicesCommand.ExecuteAsync(null);
        vm.SelectedFfbTelemetryDevice = vm.FfbTelemetryDevices.Single(d => d.InstanceGuid == "{WHEEL}");
        Assert.False(vm.FfbTelemetryIsVpforce);
        vm.SelectedFfbTelemetryDevice = vm.FfbTelemetryDevices.Single(d => d.InstanceGuid == "{RHINO}");
        Assert.True(vm.FfbTelemetryIsVpforce);
        Assert.Contains("untick", MainWindowViewModel.FfbTelemetryVpforceHint);
        Assert.True(FfbTelemetryDeviceOption.IsVpforceDevice("ffff", "2055", "Some stick"));
        Assert.False(FfbTelemetryDeviceOption.IsVpforceDevice("046D", "C21D", "Some Gamepad"));
    }

    [Fact]
    public async Task CentringSpringAndInverts_AreSavedGlobally_AndForTheDevice()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, _) = await FfbTelemetryTests.CreateLoadedVmAsync(configure: e => e.Device = RhinoGuid);
        Assert.True(vm.FfbTelemetryCentringSpring);                  // default: JoyPro provides the centring
        Assert.Equal(30, vm.FfbTelemetryCentringStiffness);
        engine.Calls.Clear();

        vm.FfbTelemetryCentringSpring = false;
        vm.FfbTelemetryCentringStiffness = 45;
        vm.FfbTelemetryInvertX = true;
        Assert.True(await vm.FlushFfbTelemetrySettingsAsync());
        List<IReadOnlyList<string>> sets = engine.Calls.Where(c => Seq(c, "ffb", "telemetry", "config", "set")).ToList();
        Assert.Contains(sets, c => Seq(c, "--param", "centring-spring=false", "--param", "centring-stiffness=0.45", "--param", "invert-x=true"));
        Assert.Contains(sets, c => Seq(c, "--device", RhinoGuid, "--param", "invert-x=true"));
    }

    [Fact]
    public async Task EachDevice_KeepsItsOwnDirections()
    {
        (MainWindowViewModel vm, _, _) = await FfbTelemetryTests.CreateLoadedVmAsync(configure: e => e.Device = RhinoGuid);
        Assert.True(vm.FfbTelemetryInvertY);                          // the config's global invert Y
        vm.FfbTelemetryInvertY = false;                               // saved for the Rhino
        vm.FfbTelemetryDevices.Add(new FfbTelemetryDeviceOption("{BBBBBBBB-1111-2222-3333-444444444444}", "Other stick", string.Empty, true));
        vm.SelectedFfbTelemetryDevice = vm.FfbTelemetryDevices.Last();
        Assert.False(vm.FfbTelemetryInvertY);                         // no entry: the global ones (follow the last change)
        vm.FfbTelemetryInvertY = true;                                // saved for the other stick
        vm.SelectedFfbTelemetryDevice = vm.FfbTelemetryDevices.Single(d => d.InstanceGuid == RhinoGuid);
        Assert.False(vm.FfbTelemetryInvertY);                         // the Rhino's own entry
    }

    // ------------------------------------------------------------------ direction test

    [Fact]
    public async Task DirectionTest_AnnouncesEachStep_AndSetsTheInverts()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, _) = await FfbTelemetryTests.CreateLoadedVmAsync(configure: e => e.Device = RhinoGuid);
        FakeTestOutput output = new();
        string? device = null;
        vm.FfbTestOutputFactory = d =>
        {
            device = d;
            return output;
        };
        vm.FfbWizardDelay = (_, _) => Task.CompletedTask;
        vm.FfbTelemetryInvertY = false;
        engine.Calls.Clear();

        vm.StartFfbDirectionTestCommand.Execute(null);
        Assert.True(vm.FfbWizardOpen);
        Assert.Equal(RhinoGuid, device);
        Assert.Equal("Start", vm.FfbWizardReadyLabel);
        Assert.True(vm.FfbWizardCanReady);
        Assert.Empty(output.Calls);                                   // nothing moves before the user is ready

        await vm.FfbWizardReadyCommand.ExecuteAsync(null);           // intro -> announce "right"
        Assert.Contains("RIGHT", vm.FfbWizardText);
        Assert.Equal("→", vm.FfbWizardGlyph);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);           // push right, then ask
        Assert.Equal(3, vm.FfbWizardChoices.Count);
        await vm.FfbWizardAnswerCommand.ExecuteAsync(vm.FfbWizardChoices.Single(c => c.Answer == FfbWizardAnswer.Right));
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);           // push forward
        await vm.FfbWizardAnswerCommand.ExecuteAsync(vm.FfbWizardChoices.Single(c => c.Answer == FfbWizardAnswer.Back));
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);           // spring on
        await vm.FfbWizardAnswerCommand.ExecuteAsync(vm.FfbWizardChoices.Single(c => c.Answer == FfbWizardAnswer.Yes));
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);           // trim check
        await vm.FfbWizardAnswerCommand.ExecuteAsync(vm.FfbWizardChoices.Single(c => c.Answer == FfbWizardAnswer.Yes));

        Assert.True(vm.FfbWizardFinished);
        Assert.False(vm.FfbTelemetryInvertX);
        Assert.True(vm.FfbTelemetryInvertY);
        Assert.False(output.ForceOn);
        Assert.Contains("push X 0.2", output.Calls);
        Assert.Contains("push Y -0.2", output.Calls);
        await WaitUntilAsync(() => output.Disposed);                 // the test process is quit
        await vm.FlushFfbTelemetrySettingsAsync();
        Assert.Contains(engine.Calls, c => Seq(c, "--device", RhinoGuid, "--param", "invert-y=true"));

        await vm.FfbWizardCloseCommand.ExecuteAsync(null);
        Assert.False(vm.FfbWizardOpen);
    }

    [Fact]
    public async Task DirectionTest_CancelStopsTheForces_AndChangesNothing()
    {
        (MainWindowViewModel vm, _, _) = await FfbTelemetryTests.CreateLoadedVmAsync();
        FakeTestOutput output = new();
        vm.FfbTestOutputFactory = _ => output;
        vm.FfbWizardDelay = (_, _) => Task.CompletedTask;
        bool invertY = vm.FfbTelemetryInvertY;
        vm.StartFfbDirectionTestCommand.Execute(null);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);
        await vm.FfbWizardAnswerCommand.ExecuteAsync(vm.FfbWizardChoices.Single(c => c.Answer == FfbWizardAnswer.Left));
        await vm.FfbWizardCancelCommand.ExecuteAsync(null);
        Assert.Equal("stop", output.Calls[^1]);
        Assert.Equal("Cancelled", vm.FfbWizardTitle);
        Assert.False(vm.FfbTelemetryInvertX);
        Assert.Equal(invertY, vm.FfbTelemetryInvertY);
    }

    [Fact]
    public async Task DirectionTest_RetryAndStartOver()
    {
        (MainWindowViewModel vm, _, _) = await FfbTelemetryTests.CreateLoadedVmAsync();
        List<FakeTestOutput> outputs = [];
        vm.FfbTestOutputFactory = _ =>
        {
            FakeTestOutput created = new();
            outputs.Add(created);
            return created;
        };
        vm.FfbWizardDelay = (_, _) => Task.CompletedTask;
        vm.StartFfbDirectionTestCommand.Execute(null);
        Assert.True(vm.FfbWizardIsIntro);
        Assert.False(vm.FfbWizardCanStartOver);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);
        Assert.False(vm.FfbWizardIsIntro);
        Assert.Equal("Ready", vm.FfbWizardReadyLabel);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);
        Assert.True(vm.FfbWizardCanRetry);
        await vm.FfbWizardRetryCommand.ExecuteAsync(null);
        Assert.True(vm.FfbWizardCanReady);
        Assert.False(vm.FfbWizardCanRetry);
        Assert.Contains("RIGHT", vm.FfbWizardText);
        await vm.FfbWizardReadyCommand.ExecuteAsync(null);
        Assert.Equal(2, outputs[0].Calls.Count(c => c == "push X 0.2"));

        await vm.FfbWizardStartOverCommand.ExecuteAsync(null);
        Assert.True(vm.FfbWizardIsIntro);
        Assert.Single(outputs);                                       // same test process while it runs

        await vm.FfbWizardCancelCommand.ExecuteAsync(null);
        await vm.FfbWizardStartOverCommand.ExecuteAsync(null);       // after the end: a fresh test process
        Assert.Equal(2, outputs.Count);
        Assert.True(vm.FfbWizardIsIntro);
        Assert.True(vm.FfbWizardOpen);
    }

    [Fact]
    public async Task DirectionTest_IsRefusedWhileJoyProDrivesTheStickForAGame()
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await FfbTelemetryTests.CreateLoadedVmAsync();
        FfbTelemetryTests.StartProcess(vm, launcher, "DCS");
        int created = 0;
        vm.FfbTestOutputFactory = _ =>
        {
            created++;
            return new FakeTestOutput();
        };
        vm.StartFfbDirectionTestCommand.Execute(null);
        Assert.Equal(0, created);
        Assert.False(vm.FfbWizardOpen);
        Assert.Contains("close the game", vm.FfbTelemetryMessage);
    }

    [Fact]
    public async Task TestProcessOutput_SendsCommands_AndReadsTheAnswers()
    {
        ReplyingLauncher launcher = new();
        using FfbTestProcessOutput output = new(launcher, "JoyPro.Ffb.exe", RhinoGuid, 777);
        await output.SetInvertAsync(false, true, CancellationToken.None);
        await output.PushAsync(FfbAxis.Y, -0.2, CancellationToken.None);
        await output.SpringAsync(0.5, 0, -0.35, CancellationToken.None);
        (double X, double Y)? position = await output.ReadPositionAsync(CancellationToken.None);
        await output.StopAsync();
        ReplyingProcess process = Assert.Single(launcher.Started);
        Assert.Equal(["test", "--parent-pid", "777", "--device", RhinoGuid], process.Arguments);
        Assert.Equal(["invert 0 1", "push Y -0.2", "spring 0.5 0 -0.35", "pos", "stop"], process.Sent);
        Assert.Equal((0.1, -0.25), position);

        process.ErrorFor = "push";
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => output.PushAsync(FfbAxis.X, 0.2, CancellationToken.None));
        Assert.Contains("held by another program", error.Message);

        output.Dispose();
        Assert.Equal("quit", process.Sent[^1]);
    }

    // ------------------------------------------------------------------ graph editor

    [Fact]
    public void Palette_OffersTheInputTrimAndInvertNodes_WithPlainLabels()
    {
        Assert.Contains(FfbGraphEditorViewModel.PaletteItems, p => p.Kind == NodeKind.Invert);
        Assert.Contains(FfbGraphEditorViewModel.PaletteItems, p => p.Kind == NodeKind.DeviceButton);
        Assert.Contains(FfbGraphEditorViewModel.PaletteItems, p => p.Kind == NodeKind.StickPosition);
        Assert.Contains(FfbGraphEditorViewModel.PaletteItems, p => p.Kind == NodeKind.Trim);

        FfbGraphEditorViewModel editor = new();
        editor.AddNode(NodeKind.ConstantForce);
        Assert.Contains(editor.Properties, p => p.Name == nameof(ConstantForceNode.Negative) && p.Label.StartsWith("Invert direction", StringComparison.Ordinal) && p.IsToggle);
        editor.AddNode(NodeKind.Spring);
        Assert.Contains(editor.Properties, p => p.Name == nameof(SpringNode.InvertCentre) && p.IsToggle);
        editor.AddNode(NodeKind.Invert);
        Assert.Contains(editor.Properties, p => p.Name == nameof(InvertNode.Enabled) && p.Label.StartsWith("Invert", StringComparison.Ordinal));
        Assert.False(editor.CanCaptureInput);
        editor.AddNode(NodeKind.DeviceButton);
        Assert.True(editor.CanCaptureInput);
    }

    [Fact]
    public async Task Capture_AssignsTheButton_AndAPressOnTheStickItselfMeansTheStick()
    {
        Queue<(string, string)?> presses = new();
        presses.Enqueue(("Throttle {11111111-2222-3333-4444-555555555555}", "JOY_BTN_POV1_U"));
        presses.Enqueue(("VPforce Rhino " + RhinoGuid, "JOY_BTN7"));
        presses.Enqueue(null);
        FfbGraphEditorViewModel editor = new()
        {
            CaptureInput = (_, _) => Task.FromResult(presses.Dequeue()),
            ForceFeedbackDevice = () => RhinoGuid
        };
        DeviceButtonNode button = (DeviceButtonNode)editor.AddNode(NodeKind.DeviceButton);
        await editor.CaptureSelectedInputCommand.ExecuteAsync(null);
        Assert.Equal(("Throttle {11111111-2222-3333-4444-555555555555}", "JOY_BTN_POV1_U"), (button.Device, button.Input));
        Assert.True(editor.IsDirty);
        await editor.CaptureSelectedInputCommand.ExecuteAsync(null);
        Assert.Equal((string.Empty, "JOY_BTN7"), (button.Device, button.Input));
        Assert.Contains("force feedback stick", editor.Message);
        await editor.CaptureSelectedInputCommand.ExecuteAsync(null);  // nothing pressed: unchanged
        Assert.Equal("JOY_BTN7", button.Input);
        Assert.Contains("unchanged", editor.Message);
    }

    [Fact]
    public async Task AssignTrimButtons_OnTheBuiltInGraph_AsksForEachInput_AndSkipsOnTimeout()
    {
        Queue<(string, string)?> presses = new();
        presses.Enqueue(("Stick " + RhinoGuid, "JOY_BTN4"));          // here
        presses.Enqueue(("Stick " + RhinoGuid, "JOY_BTN_POV1_U"));    // up
        presses.Enqueue(("Stick " + RhinoGuid, "JOY_BTN_POV1_D"));    // down
        presses.Enqueue(null);                                         // left: skipped
        presses.Enqueue(null);                                         // right: skipped
        presses.Enqueue(("Throttle", "JOY_BTN12"));                    // reset
        FfbGraphEditorViewModel editor = new()
        {
            CaptureInput = (_, _) => Task.FromResult(presses.Dequeue()),
            ForceFeedbackDevice = () => RhinoGuid
        };
        TrimNode trim = editor.Graph.Nodes.OfType<TrimNode>().Single();
        editor.SelectedNode = trim;
        Assert.True(editor.CanAssignTrimButtons);
        await editor.AssignTrimButtonsCommand.ExecuteAsync(null);

        DeviceButtonNode ButtonOf(string port) =>
            (DeviceButtonNode)editor.Graph.FindNode(editor.Graph.Connections.Single(c => c.ToNode == trim.Id && c.ToPort == port).FromNode)!;
        Assert.Equal((string.Empty, "JOY_BTN4"), (ButtonOf("here").Device, ButtonOf("here").Input));
        Assert.Equal("JOY_BTN_POV1_U", ButtonOf("up").Input);
        Assert.Equal("JOY_BTN_POV1_D", ButtonOf("down").Input);
        Assert.Equal(string.Empty, ButtonOf("left").Input);
        Assert.Equal(("Throttle", "JOY_BTN12"), (ButtonOf("reset").Device, ButtonOf("reset").Input));
        Assert.Contains("4 of 6", editor.Message);
        Assert.Empty(presses);
    }

    [Fact]
    public async Task AssignTrimButtons_AddsMissingButtonNodes()
    {
        FfbGraphEditorViewModel editor = new()
        {
            CaptureInput = (_, _) => Task.FromResult<(string, string)?>(("Throttle", "JOY_BTN1")),
        };
        editor.SetGraph(new FfbGraph(), string.Empty);
        TrimNode trim = (TrimNode)editor.AddNode(NodeKind.Trim);
        await editor.AssignTrimButtonsCommand.ExecuteAsync(null);
        Assert.Equal(6, editor.Graph.Nodes.OfType<DeviceButtonNode>().Count());
        Assert.Equal(6, editor.Graph.Connections.Count(c => c.ToNode == trim.Id));
    }

    // ------------------------------------------------------------------ fakes

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out.");
    }

    internal sealed class FakeTestOutput : IFfbTestOutput, IDisposable
    {
        public List<string> Calls { get; } = [];

        public bool ForceOn { get; private set; }

        public bool Disposed { get; private set; }

        public Task SetInvertAsync(bool invertX, bool invertY, CancellationToken token)
        {
            Calls.Add("invert " + (invertX ? 1 : 0) + " " + (invertY ? 1 : 0));
            return Task.CompletedTask;
        }

        public Task PushAsync(FfbAxis axis, double magnitude, CancellationToken token)
        {
            Calls.Add(FormattableString.Invariant($"push {axis} {magnitude:0.##}"));
            ForceOn = true;
            return Task.CompletedTask;
        }

        public Task SpringAsync(double stiffness, double centreX, double centreY, CancellationToken token)
        {
            Calls.Add(FormattableString.Invariant($"spring {stiffness:0.##} {centreX:0.##} {centreY:0.##}"));
            ForceOn = true;
            return Task.CompletedTask;
        }

        public Task<(double X, double Y)?> ReadPositionAsync(CancellationToken token) => Task.FromResult<(double X, double Y)?>((0, 0));

        public Task StopAsync()
        {
            Calls.Add("stop");
            ForceOn = false;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ReplyingLauncher : IFfbHostLauncher
    {
        public List<ReplyingProcess> Started { get; } = [];

        public IFfbHostProcess Start(string exePath, IReadOnlyList<string> arguments)
        {
            ReplyingProcess process = new(arguments.ToArray());
            Started.Add(process);
            return process;
        }

        public Task<FfbToolResult> RunToolAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfbToolResult(0, string.Empty, string.Empty));
    }

    // Answers like JoyPro.Ffb test: one JSON line per command (a log line first, which is skipped).
    private sealed class ReplyingProcess(string[] arguments) : IFfbHostProcess
    {
        private bool exited;

        public string[] Arguments { get; } = arguments;

        public List<string> Sent { get; } = [];

        public string? ErrorFor { get; set; }

        public int Id => 1;

        public bool HasExited => exited;

        public event Action<string>? LineReceived;

        public event Action<string>? ErrorLineReceived;

        public event Action? Exited;

        public bool Send(string command)
        {
            if (exited)
                return false;
            Sent.Add(command);
            if (command == "quit")
            {
                exited = true;
                Exited?.Invoke();
                return true;
            }
            LineReceived?.Invoke("{\"type\":\"log\",\"message\":\"test: Rhino\"}");
            if (ErrorFor != null && command.StartsWith(ErrorFor, StringComparison.Ordinal))
                LineReceived?.Invoke("{\"type\":\"error\",\"message\":\"Acquire failed: the device is held by another program\"}");
            else if (command == "pos")
                LineReceived?.Invoke("{\"type\":\"position\",\"valid\":true,\"x\":0.1,\"y\":-0.25}");
            else
                LineReceived?.Invoke("{\"type\":\"ok\"}");
            _ = ErrorLineReceived;
            return true;
        }

        public bool WaitForExit(int milliseconds) => exited;

        public void Kill() => exited = true;

        public void Dispose()
        {
        }
    }
}
