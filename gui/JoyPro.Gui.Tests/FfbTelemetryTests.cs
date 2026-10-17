using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;
using static JoyPro.Gui.Tests.AxisTuningTests;

namespace JoyPro.Gui.Tests;

// The "Force feedback (telemetry)" page against a fake engine (engine ffb telemetry status / config / setup) and a
// fake JoyPro.Ffb: no test starts the real process or looks at the PC's game processes.
[SupportedOSPlatform("windows")]
public sealed class FfbTelemetryTests
{
    [Fact]
    public async Task Status_FillsThePage_WithoutSendingAnythingBack()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, _) = await CreateLoadedVmAsync();

        Assert.Contains(engine.Calls, c => Seq(c, "ffb", "telemetry", "status"));
        Assert.DoesNotContain(engine.Calls, c => Seq(c, "config", "set"));
        Assert.False(vm.FfbTelemetryEnabled);
        Assert.Equal(80, vm.FfbTelemetryStrength);
        Assert.Equal(60, vm.FfbTelemetryMaxForce);
        Assert.True(vm.FfbTelemetryInvertY);
        Assert.Equal("{DEV-1}", vm.SelectedFfbTelemetryDevice?.InstanceGuid);
        Assert.Contains("telemetry-ffb.json", vm.FfbTelemetryConfigPath);

        FfbTelemetryGameItem dcs = Game(vm, "DCS");
        Assert.True(dcs.Enabled);
        Assert.Equal(120, dcs.Strength);
        Assert.Equal("viper.jpffb.json", dcs.Graph?.Value);
        Assert.Contains("file missing", dcs.Graph?.Label);
        Assert.Equal(FfbEffectNames.All.Count, dcs.Effects.Count);
        Assert.False(dcs.Effects.Single(e => e.Name == "buffet").Enabled);
        Assert.Equal(50, dcs.Effects.Single(e => e.Name == "guns").Strength);
        FfbTelemetryAircraftItem viper = Assert.Single(dcs.Aircraft);
        Assert.Equal("F-16C_50", viper.Name);
        Assert.Equal(80, viper.Strength);
        Assert.True(viper.Graph?.IsBuiltIn);
        Assert.True(dcs.SetupInstalled);
        Assert.Equal("UDP 41992", dcs.Endpoint);

        FfbTelemetryGameItem wt = Game(vm, "WarThunder");
        Assert.False(wt.SetupNeeded);
        Assert.False(wt.Enabled);
        Assert.Contains("Nothing to set up", wt.SetupStateText);
        Assert.Equal(FfbTelemetryOff, vm.FfbTelemetryStatusText);
    }

    [Fact]
    public async Task SettingsChanges_AreSentAsConfigSetCalls_PerScope_ThenReload()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, FakeFfbLauncher launcher) = await CreateLoadedVmAsync();
        FakeFfbHostProcess process = StartProcess(vm, launcher, "DCS");
        engine.Calls.Clear();

        vm.FfbTelemetryStrength = 50;
        vm.FfbTelemetryMaxForce = 40;
        vm.FfbTelemetryInvertX = true;
        FfbTelemetryGameItem dcs = Game(vm, "DCS");
        dcs.Strength = 150;
        dcs.Effects.Single(e => e.Name == "buffet").Enabled = true;
        dcs.Effects.Single(e => e.Name == "buffet").Strength = 30;
        dcs.Aircraft.Single().Strength = 120;
        dcs.Graph = vm.FfbTelemetryGraphOptions.First(o => o.IsBuiltIn);
        Assert.Empty(engine.Calls);

        Assert.True(await vm.FlushFfbTelemetrySettingsAsync());
        List<IReadOnlyList<string>> sets = engine.Calls.Where(c => Seq(c, "ffb", "telemetry", "config", "set")).ToList();
        Assert.Equal(4, sets.Count);
        // Global scope (no --game): the switch from StartProcess, then the three changes, in one call.
        Assert.Equal(["ffb", "telemetry", "config", "set", "--param", "enabled=true", "--param", "strength=0.5", "--param", "max-force=0.4", "--param", "invert-x=true"], Tail(sets[0], 12));
        Assert.Equal(["--game", "DCS", "--param", "strength=1.5", "--param", "graph="], Tail(sets[1], 6));
        Assert.Equal(["--game", "DCS", "--effect", "buffet", "--param", "enabled=true", "--param", "strength=0.3"], Tail(sets[2], 8));
        Assert.Equal(["--game", "DCS", "--plane", "F-16C_50", "--param", "strength=1.2"], Tail(sets[3], 6));
        Assert.Equal([HostProtocol.Reload], process.Sent);

        // Nothing pending: no call.
        engine.Calls.Clear();
        Assert.True(await vm.FlushFfbTelemetrySettingsAsync());
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task Aircraft_AddAndRemove()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, _) = await CreateLoadedVmAsync();
        vm.SelectedFfbTelemetryGame = Game(vm, "DCS");
        engine.Calls.Clear();

        vm.FfbTelemetryNewAircraft = "A-10C_2";
        vm.AddFfbTelemetryAircraftCommand.Execute(null);
        await vm.FlushFfbTelemetrySettingsAsync();
        Assert.Contains(engine.Calls, c => Seq(c, "config", "set", "--game", "DCS", "--plane", "A-10C_2", "--param", "strength=1"));
        FfbTelemetryAircraftItem added = Game(vm, "DCS").Aircraft.Single(a => a.Name == "A-10C_2");

        await vm.RemoveFfbTelemetryAircraftCommand.ExecuteAsync(added);
        Assert.Contains(engine.Calls, c => Seq(c, "ffb", "telemetry", "config", "clear", "--game", "DCS", "--plane", "A-10C_2"));
        Assert.DoesNotContain(Game(vm, "DCS").Aircraft, a => a.Name == "A-10C_2");
    }

    [Fact]
    public async Task SetupButtons_CallTheEngine_AndShowTheChanges()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, _) = await CreateLoadedVmAsync();
        vm.SelectedFfbTelemetryGame = Game(vm, "IL2Game");
        Assert.True(vm.FfbTelemetryGameNeedsSetup);
        engine.Calls.Clear();

        await vm.FfbTelemetrySetupCommand.ExecuteAsync("dry-run");
        Assert.True(Seq(engine.Calls.Single(), "ffb", "telemetry", "setup", "il2", "--dry-run"));
        Assert.Contains("Dry run - would change:", vm.FfbTelemetrySetupChanges);
        Assert.Contains("startup.cfg: addr", vm.FfbTelemetrySetupChanges);
        Assert.False(Game(vm, "IL2Game").SetupInstalled);

        engine.Calls.Clear();
        await vm.FfbTelemetrySetupCommand.ExecuteAsync("install");
        IReadOnlyList<string> install = engine.Calls.Single();
        Assert.True(Seq(install, "ffb", "telemetry", "setup", "il2"));
        Assert.DoesNotContain("--dry-run", install);
        Assert.DoesNotContain("--remove", install);
        Assert.StartsWith("Changed:", vm.FfbTelemetrySetupChanges);
        Assert.True(Game(vm, "IL2Game").SetupInstalled);

        engine.Calls.Clear();
        await vm.FfbTelemetrySetupCommand.ExecuteAsync("remove");
        Assert.True(Seq(engine.Calls.Single(), "ffb", "telemetry", "setup", "il2", "--remove"));
        Assert.False(Game(vm, "IL2Game").SetupInstalled);

        // A failure shows the engine's errors.
        engine.FailSetup = true;
        await vm.FfbTelemetrySetupCommand.ExecuteAsync("install");
        Assert.Contains("Error: IL-2 is running", vm.FfbTelemetrySetupChanges);

        // War Thunder: nothing to set up, no engine call.
        vm.SelectedFfbTelemetryGame = Game(vm, "WarThunder");
        Assert.False(vm.FfbTelemetryGameNeedsSetup);
        engine.Calls.Clear();
        await vm.FfbTelemetrySetupCommand.ExecuteAsync("install");
        Assert.Empty(engine.Calls);
        Assert.Contains("Nothing to set up", vm.FfbTelemetrySetupChanges);
        Assert.Equal("warthunder", MainWindowViewModel.FfbTelemetrySetupId("WarThunder"));
        Assert.Equal("il2korea", MainWindowViewModel.FfbTelemetrySetupId("IL2Korea"));
    }

    [Fact]
    public void StatusLine_ComesFromTheProtocolLines()
    {
        TelemetryFfbStatus active = new(TelemetryFfbState.Active, "active - F-16C_50", "DCS", "F-16C_50", "Flight (built-in)", 120, "UDP 41992");
        Assert.Equal("active - F-16C_50", MainWindowViewModel.FfbTelemetryReasonFromStatus(HostProtocol.Parse(HostProtocol.StatusLine(active, "Rhino"))!));
        TelemetryFfbStatus silent = new(TelemetryFfbState.NoTelemetry, "idle - no telemetry on UDP 41992", "DCS", null, "Flight (built-in)", 0, "UDP 41992");
        Assert.Equal("idle - no telemetry on UDP 41992", MainWindowViewModel.FfbTelemetryReasonFromStatus(HostProtocol.Parse(HostProtocol.StatusLine(silent, "Rhino"))!));
        Assert.Equal("active - A-10C_2", MainWindowViewModel.FfbTelemetryReasonFromStatus(new JsonObject { ["type"] = "status", ["state"] = "active", ["aircraft"] = "A-10C_2" }));
    }

    [Fact]
    public async Task StatusLine_FollowsTheProcess_AndAFaultIsNotRestartedForTheSameGame()
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateLoadedVmAsync();
        FakeFfbHostProcess process = StartProcess(vm, launcher, "DCS");
        Assert.Equal("starting - DCS World", vm.FfbTelemetryStatusText);

        process.Emit(Status(TelemetryFfbState.NoTelemetry, "idle - no telemetry on UDP 41992", null));
        Assert.Equal("idle - no telemetry on UDP 41992", vm.FfbTelemetryStatusText);
        process.Emit(Status(TelemetryFfbState.Active, "active - F-16C_50", "F-16C_50"));
        Assert.Equal("active - F-16C_50", vm.FfbTelemetryStatusText);
        Assert.Equal("F-16C_50", vm.FfbTelemetryCurrentAircraft);
        process.Emit("not json at all");
        process.Emit(HostProtocol.LogLine("hello from the process"));
        Assert.Contains(vm.FfbTelemetryLog, l => l.EndsWith("hello from the process", StringComparison.Ordinal));

        process.Emit(Status(TelemetryFfbState.Faulted, "stopped after an error: device lost", "F-16C_50"));
        process.Emit(HostProtocol.ExitLine("stopped"));
        process.Exit();
        Assert.False(vm.FfbTelemetryProcessRunning);
        Assert.Equal("stopped after an error: device lost", vm.FfbTelemetryStatusText);
        Assert.True(process.Disposed);

        // Same game process: no restart loop. A new game process: started again.
        vm.PollFfbTelemetryGamesOnce();
        Assert.Single(launcher.Started);
        launcher.GamePid = 5151;
        vm.PollFfbTelemetryGamesOnce();
        Assert.Equal(2, launcher.Started.Count);

        // The game exits: the process ends itself, the page waits for the game again.
        FakeFfbHostProcess second = launcher.Started[1];
        second.Emit(HostProtocol.ExitLine("game exited"));
        launcher.RunningGames.Clear();
        second.Exit();
        Assert.StartsWith("idle - waiting for DCS.exe / DCS_mt.exe / Il-2.exe", vm.FfbTelemetryStatusText);
    }

    [Fact]
    public async Task ProcessManager_RunsOnlyWhileEnabled_AndStartsOnlyWhenAGameRuns()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, FakeFfbLauncher launcher) = await CreateLoadedVmAsync();
        launcher.RunningGames.Add("DCS_mt");

        // Disabled: no polling, not even a process lookup.
        vm.PollFfbTelemetryGamesOnce();
        Assert.False(vm.FfbTelemetryPolling);
        Assert.Equal(0, launcher.Lookups);
        Assert.Empty(launcher.Started);

        vm.FfbTelemetryEnabled = true;
        Assert.True(vm.FfbTelemetryPolling);
        FakeFfbHostProcess process = Assert.Single(launcher.Started);
        Assert.Equal(["run", "--game", "DCS", "--game-pid", "4242", "--parent-pid", "777", "--protocol"], process.Arguments);
        Assert.Equal(vm.FfbTelemetryExePath, process.ExePath);
        Assert.True(vm.FfbTelemetryProcessRunning);
        Assert.True(Game(vm, "DCS").Running);

        // While it runs nothing is looked up and nothing else is started.
        int lookups = launcher.Lookups;
        vm.PollFfbTelemetryGamesOnce();
        Assert.Equal(lookups, launcher.Lookups);
        Assert.Single(launcher.Started);

        await vm.FlushFfbTelemetrySettingsAsync();
        Assert.Contains(engine.Calls, c => Seq(c, "config", "set", "--param", "enabled=true"));

        // Disabled again: "stop", killed when it does not end, no more polling.
        process.IgnoreStop = true;
        vm.FfbTelemetryEnabled = false;
        Assert.False(vm.FfbTelemetryPolling);
        Assert.False(vm.FfbTelemetryProcessRunning);
        Assert.Contains(HostProtocol.Stop, process.Sent);
        await WaitUntilAsync(() => process.Disposed);
        Assert.True(process.Killed);
        Assert.Equal(FfbTelemetryOff, vm.FfbTelemetryStatusText);
        vm.PollFfbTelemetryGamesOnce();
        Assert.Single(launcher.Started);

        // Enabled without a running game: waiting; a disabled game is not looked up.
        launcher.RunningGames.Clear();
        launcher.LookedUp.Clear();
        Game(vm, "WarThunder").Enabled = false;
        vm.FfbTelemetryEnabled = true;
        Assert.Single(launcher.Started);
        Assert.DoesNotContain(launcher.LookedUp, names => names.Contains("aces"));
        Assert.StartsWith("idle - waiting for DCS.exe", vm.FfbTelemetryStatusText);
        Assert.DoesNotContain("aces.exe", vm.FfbTelemetryStatusText);

        // Shutdown stops a running process and waits for it.
        launcher.RunningGames.Add("Il-2");
        vm.PollFfbTelemetryGamesOnce();
        FakeFfbHostProcess il2 = launcher.Started.Last();
        Assert.Equal("IL2Game", il2.Arguments[2]);
        vm.StopFfbTelemetryForShutdown();
        Assert.Contains(HostProtocol.Stop, il2.Sent);
        Assert.True(il2.Disposed);
        Assert.False(il2.Killed);
        Assert.False(vm.FfbTelemetryPolling);
    }

    [Fact]
    public async Task Watch_IsOnOnlyWhileTheEditorOrInspectorIsVisible()
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateLoadedVmAsync(UiStage.Start);
        vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryEditorTab;
        Assert.False(vm.FfbGraphPreviewRunning);
        FakeFfbHostProcess process = StartProcess(vm, launcher, "DCS");

        // Hidden page: no watch traffic at all.
        Assert.Empty(process.Sent);
        process.Emit(HostProtocol.ValuesLine("nodes", new Dictionary<string, double> { ["x"] = 0.5 }));
        Assert.Empty(vm.FfbGraphEditor.LiveValues);

        vm.CurrentStage = UiStage.FfbTelemetry;
        Assert.Equal([HostProtocol.WatchOn], process.Sent);
        FfbNode node = vm.FfbGraphEditor.Graph.Nodes[0];
        process.Emit(HostProtocol.ValuesLine("nodes", new Dictionary<string, double> { [node.Id] = 0.42 }));
        Assert.Equal(0.42, vm.FfbGraphEditor.LiveValues[node.Id], 5);

        vm.SetMainWindowMinimized(true);
        Assert.Equal([HostProtocol.WatchOn, HostProtocol.WatchOff], process.Sent);
        Assert.Empty(vm.FfbGraphEditor.LiveValues);
        vm.SetMainWindowMinimized(false);
        vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetrySettingsTab;
        vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryInspectorTab;
        vm.CurrentStage = UiStage.Hud;
        Assert.Equal([HostProtocol.WatchOn, HostProtocol.WatchOff, HostProtocol.WatchOn, HostProtocol.WatchOff, HostProtocol.WatchOn, HostProtocol.WatchOff], process.Sent);
        Assert.False(vm.FfbGraphPreviewRunning);

        // Without the process: the local preview runs only while the editor is visible.
        process.Exit();
        Assert.False(vm.FfbGraphPreviewRunning);
        vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryEditorTab;
        Assert.False(vm.FfbGraphPreviewRunning);
        vm.CurrentStage = UiStage.FfbTelemetry;
        Assert.True(vm.FfbGraphPreviewRunning);
        vm.SetMainWindowMinimized(true);
        Assert.False(vm.FfbGraphPreviewRunning);
        vm.SetMainWindowMinimized(false);
        Assert.True(vm.FfbGraphPreviewRunning);
        vm.CurrentStage = UiStage.Start;
        Assert.False(vm.FfbGraphPreviewRunning);
    }

    [Fact]
    public async Task Inspector_ShowsTheLatestTelemetry_WithTitlesAndAFilter()
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateLoadedVmAsync();
        FakeFfbHostProcess process = StartProcess(vm, launcher, "DCS");
        vm.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryInspectorTab;
        process.Emit(HostProtocol.ValuesLine("telemetry", new Dictionary<string, double> { ["ac.ias"] = 154.25, ["dcs.mach"] = 0.5, ["raw.unknown"] = 3 }, 12.3));

        Assert.Equal(["ac.ias", "dcs.mach", "raw.unknown"], vm.FfbTelemetryFields.Select(f => f.Key).ToArray());
        FfbTelemetryFieldItem ias = vm.FfbTelemetryFields[0];
        Assert.Equal("Indicated airspeed", ias.Title);
        Assert.Equal("m/s", ias.Unit);
        Assert.Equal("154.25", ias.ValueText);
        Assert.Equal("3 of 3 fields", vm.FfbTelemetryFieldCount);

        vm.FfbTelemetryFieldFilter = "airspeed";
        Assert.Equal(["ac.ias"], vm.FfbTelemetryFields.Select(f => f.Key).ToArray());

        // Raw keys seen live are offered for the Telemetry node.
        Assert.Contains("raw.unknown", vm.FfbGraphEditor.TelemetryKeyOptions);
        Assert.Contains("ac.aoa", vm.FfbGraphEditor.TelemetryKeyOptions);
    }

    [Fact]
    public async Task Devices_Refresh_PicksTheFirstWithForceAxes_WhenNoneIsSet()
    {
        (MainWindowViewModel vm, FfbTelemetryFakeEngine engine, FakeFfbLauncher launcher) = await CreateLoadedVmAsync(configure: e => e.Device = string.Empty);
        // Opening the page lists the devices at once (read-only) and offers the first one with force axes.
        for (int i = 0; i < 200 && vm.SelectedFfbTelemetryDevice == null; i++)
            await Task.Delay(10);
        Assert.Equal("{RHINO}", vm.SelectedFfbTelemetryDevice?.InstanceGuid);
        Assert.Equal(["devices", "--json"], launcher.ToolCalls.Single());
        engine.Calls.Clear();

        await vm.RefreshFfbTelemetryDevicesCommand.ExecuteAsync(null);
        Assert.Equal(["devices", "--json"], launcher.ToolCalls.Last());
        Assert.Equal(2, vm.FfbTelemetryDevices.Count);
        Assert.Equal("{RHINO}", vm.SelectedFfbTelemetryDevice?.InstanceGuid);
        Assert.Contains("2 force feedback device(s)", vm.FfbTelemetryDeviceInfo);
        Assert.Contains("one warning", vm.FfbTelemetryDeviceInfo);

        await vm.FlushFfbTelemetrySettingsAsync();
        Assert.Contains(engine.Calls, c => Seq(c, "config", "set", "--param", "device={RHINO}", "--param", "device-name=VPforce Rhino FFB Joystick"));
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task SavingTheGraphOfTheRunningGame_SendsReload()
    {
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateLoadedVmAsync();
        FakeFfbHostProcess process = StartProcess(vm, launcher, "DCS");
        FfbGraphEditorViewModel editor = vm.FfbGraphEditor;
        try
        {
            editor.SaveAsName = "other";
            editor.SaveAsCommand.Execute(null);
            Assert.Empty(process.Sent);
            Assert.Contains(vm.FfbTelemetryGraphOptions, o => o.Value == "other.jpffb.json");

            // DCS uses viper.jpffb.json: saving it reloads JoyPro.Ffb, and the option is no longer "missing".
            editor.SaveAsName = "viper";
            editor.SaveAsCommand.Execute(null);
            Assert.Equal([HostProtocol.Reload], process.Sent);
            Assert.Equal("viper.jpffb.json", Game(vm, "DCS").Graph?.Label);
        }
        finally
        {
            File.Delete(Path.Combine(FfbTelemetryServices.GraphFolder(), "viper.jpffb.json"));
            File.Delete(Path.Combine(FfbTelemetryServices.GraphFolder(), "other.jpffb.json"));
        }
    }

    [Fact]
    public void Editor_AddConnectRefuseDelete()
    {
        FfbGraphEditorViewModel editor = new();
        editor.SetGraph(new FfbGraph { Name = "test" }, string.Empty);
        FfbNode constant = editor.AddNode(NodeKind.Constant, 0, 0);
        FfbNode curve = editor.AddNode(NodeKind.Curve, 300, 0);
        FfbNode group = editor.AddNode(NodeKind.Group, 600, 0);
        FfbNode vector = editor.AddNode(NodeKind.MakeVec2, 600, 200);
        Assert.Equal(4, editor.Graph.Nodes.Count);
        Assert.Same(vector, editor.SelectedNode);
        Assert.True(editor.IsDirty);

        Assert.Equal(ConnectResult.Ok, editor.Connect(constant.Id, "value", curve.Id, "in"));
        Assert.Equal(ConnectResult.Ok, editor.Connect(curve.Id, "out", group.Id, "in"));
        Assert.Equal(ConnectResult.Ok, editor.Connect(constant.Id, "value", group.Id, "in"));
        Assert.Equal(3, editor.Graph.Connections.Count);

        // A loop is refused with the reason.
        Assert.Equal(ConnectResult.Cycle, editor.Connect(group.Id, "out", curve.Id, "in"));
        Assert.Contains("Cycle", editor.Message);
        Assert.Equal(ConnectResult.Cycle, editor.CheckConnection(group.Id, "out", curve.Id, "in"));
        Assert.Equal(ConnectResult.TypeMismatch, editor.Connect(vector.Id, "vec", curve.Id, "in"));
        Assert.Contains("TypeMismatch", editor.Message);
        Assert.Equal(3, editor.Graph.Connections.Count);

        // Delete a selected wire, then a node with its wires.
        editor.SelectedConnection = editor.Graph.Connections.First(c => c.FromNode == constant.Id && c.ToNode == group.Id);
        Assert.Null(editor.SelectedNode);
        editor.DeleteSelectedCommand.Execute(null);
        Assert.Equal(2, editor.Graph.Connections.Count);
        editor.SelectedNode = curve;
        editor.DeleteSelectedCommand.Execute(null);
        Assert.Equal(3, editor.Graph.Nodes.Count);
        Assert.Empty(editor.Graph.Connections);
        Assert.Null(editor.SelectedNode);

        editor.MoveNode(group, 123.4, 56.7);
        Assert.Equal(123, group.X);
        Assert.Equal(57, group.Y);

        editor.SelectedPaletteItem = FfbGraphEditorViewModel.PaletteItems.Single(p => p.Kind == NodeKind.Periodic);
        editor.AddSelectedPaletteNodeCommand.Execute(null);
        Assert.IsType<PeriodicNode>(editor.SelectedNode);
        Assert.All(Enum.GetValues<NodeKind>(), kind => Assert.Contains(FfbGraphEditorViewModel.PaletteItems, p => p.Kind == kind));
    }

    [Fact]
    public void Editor_Properties_EditTheSelectedNode()
    {
        FfbGraphEditorViewModel editor = new();
        editor.SetGraph(new FfbGraph(), string.Empty);
        ConstantNode constant = (ConstantNode)editor.AddNode(NodeKind.Constant);
        Assert.Equal(["Title", "Value", "Active"], editor.Properties.Select(p => p.Name).ToArray());
        int version = editor.Version;
        editor.Properties.Single(p => p.Name == "Value").Text = "0.75";
        Assert.Equal(0.75, constant.Value);
        Assert.True(editor.Version > version);
        editor.Properties.Single(p => p.Name == "Value").Text = "abc";
        Assert.True(editor.Properties.Single(p => p.Name == "Value").HasError);
        Assert.Equal(0.75, constant.Value);
        editor.Properties.Single(p => p.Name == "Active").IsOn = false;
        Assert.False(constant.Active);

        PeriodicNode periodic = (PeriodicNode)editor.AddNode(NodeKind.Periodic);
        FfbNodePropertyItem waveform = editor.Properties.Single(p => p.Name == "Waveform");
        Assert.True(waveform.IsChoice);
        waveform.SelectedOption = "Square";
        Assert.Equal(JoyPro.Ffb.Core.Output.FfbWaveform.Square, periodic.Waveform);
        FfbNodePropertyItem effect = editor.Properties.Single(p => p.Name == "Effect");
        Assert.Contains("buffet", effect.Options);
        effect.SelectedOption = "guns";
        Assert.Equal("guns", periodic.Effect);
        editor.Properties.Single(p => p.Name == "Slot").Text = "2.6";
        Assert.Equal(3, periodic.Slot);
        Assert.Equal("Min frequency Hz", editor.Properties.Single(p => p.Name == "MinFrequencyHz").Label);
        editor.Properties.Single(p => p.Name == "input:frequency").Text = "25";
        Assert.Equal(25, periodic.InputDefaults["frequency"]);

        // A telemetry key takes its range from the descriptor.
        TelemetryNode telemetry = (TelemetryNode)editor.AddNode(NodeKind.Telemetry);
        FfbNodePropertyItem key = editor.Properties.Single(p => p.Name == "Key");
        Assert.True(key.IsTelemetryKey);
        key.Text = "ac.aoa";
        Assert.Equal("ac.aoa", telemetry.Key);
        Assert.Equal(-10, telemetry.Minimum);
        Assert.Equal(30, telemetry.Maximum);
        Assert.Equal("30", editor.Properties.Single(p => p.Name == "Maximum").Text);
    }

    [Fact]
    public void Editor_SaveLoadRoundTrip_InTheGraphsFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ffb-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            FfbGraphEditorViewModel editor = new() { GraphFolderProvider = () => folder };
            editor.SaveAsName = "  ";
            editor.SaveCommand.Execute(null);
            Assert.False(Directory.Exists(folder));
            Assert.Contains("file name", editor.Message);

            editor.NewFromBuiltInCommand.Execute(null);
            FfbNode first = editor.Graph.Nodes[0];
            editor.MoveNode(first, 77, 88);
            editor.SaveAsName = "My: viper";
            editor.SaveAsCommand.Execute(null);
            Assert.Equal("My_ viper.jpffb.json", editor.CurrentFile);
            Assert.False(editor.IsDirty);
            Assert.True(File.Exists(Path.Combine(folder, "My_ viper.jpffb.json")));
            Assert.Equal(["My_ viper.jpffb.json"], editor.GraphFiles);

            int nodes = editor.Graph.Nodes.Count;
            int wires = editor.Graph.Connections.Count;
            editor.NewFromBuiltInCommand.Execute(null);
            editor.SelectedGraphFile = "My_ viper.jpffb.json";
            editor.LoadSelectedGraphCommand.Execute(null);
            Assert.Equal("My_ viper.jpffb.json", editor.CurrentFile);
            Assert.Equal(nodes, editor.Graph.Nodes.Count);
            Assert.Equal(wires, editor.Graph.Connections.Count);
            FfbNode loaded = editor.Graph.FindNode(first.Id)!;
            Assert.Equal(77, loaded.X);
            Assert.Equal(88, loaded.Y);
            Assert.False(editor.Graph.Enabled);

            // Save writes the current file again.
            editor.MoveNode(loaded, 10, 10);
            editor.SaveCommand.Execute(null);
            Assert.Equal(10, FfbGraphPersistence.Load(Path.Combine(folder, "My_ viper.jpffb.json")).Graph.FindNode(first.Id)!.X);

            Assert.False(editor.LoadFile("missing.jpffb.json"));
            Assert.Contains("Cannot load", editor.Message);
            Assert.Equal("a.jpffb.json", FfbGraphEditorViewModel.NormaliseFileName("a.json"));
            Assert.Null(FfbGraphEditorViewModel.NormaliseFileName(" . "));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Editor_CurvePoints_AddMoveRemove()
    {
        FfbGraphEditorViewModel editor = new();
        editor.SetGraph(new FfbGraph(), string.Empty);
        Assert.Equal(-1, editor.AddCurvePoint(0.5, 0.5));
        CurveNode curve = (CurveNode)editor.AddNode(NodeKind.Curve);
        Assert.True(editor.SelectedIsCurve);
        Assert.Same(curve.Curve, editor.SelectedCurve);

        int index = editor.AddCurvePoint(0.5, 0.2);
        Assert.Equal(1, index);
        Assert.Equal(3, curve.Curve.Nodes.Count);
        Assert.Equal(1, editor.SelectedCurvePoint);
        Assert.Equal(0.2, curve.Curve.Evaluate(0.5), 2);

        editor.MoveCurveAnchor(1, 0.6, 0.9);
        Assert.Equal(new CurvePoint(0.6, 0.9), curve.Curve.Nodes[1].Position);
        editor.MoveCurveAnchor(0, 0.3, 0.1);
        Assert.Equal(0, curve.Curve.Nodes[0].Position.X);
        editor.MoveCurveHandle(1, inHandle: true, 0.9, 0.5);
        Assert.Equal(0.6, curve.Curve.Nodes[1].InHandle.X);

        // The ends stay; a middle point goes.
        Assert.False(editor.RemoveCurvePoint(0));
        Assert.True(editor.RemoveCurvePoint(1));
        Assert.Equal(2, curve.Curve.Nodes.Count);
        Assert.Equal(-1, editor.SelectedCurvePoint);
        editor.SelectedCurvePoint = 1;
        editor.RemoveSelectedCurvePointCommand.Execute(null);
        Assert.Contains("middle point", editor.Message);
    }

    [Fact]
    public void Editor_LocalPreview_EvaluatesConstantsWithoutTheGame()
    {
        FfbGraphEditorViewModel editor = new();
        editor.SetGraph(new FfbGraph(), string.Empty);
        ConstantNode constant = (ConstantNode)editor.AddNode(NodeKind.Constant);
        constant.Value = 0.5;
        MultiplyNode multiply = (MultiplyNode)editor.AddNode(NodeKind.Multiply);
        multiply.InputDefaults["b"] = 0.5;
        TelemetryNode telemetry = (TelemetryNode)editor.AddNode(NodeKind.Telemetry);
        telemetry.Key = "ac.ias";
        editor.Connect(constant.Id, "value", multiply.Id, "a");
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.EvaluateLocally(0);
        Assert.Equal(0.5, editor.LiveValues[constant.Id]);
        Assert.Equal(0.25, editor.LiveValues[multiply.Id]);
        Assert.Equal(0, editor.LiveValues[telemetry.Id]);
        Assert.True(changes > 0);

        // An edit is picked up by the next tick (a fresh private copy of the graph).
        editor.Properties.Clear();
        editor.SelectedNode = constant;
        editor.Properties.Single(p => p.Name == "Value").Text = "1";
        editor.EvaluateLocally(0.1);
        Assert.Equal(0.5, editor.LiveValues[multiply.Id]);
        editor.ClearLiveValues();
        Assert.Empty(editor.LiveValues);
    }

    [Fact]
    public void DeviceList_IsParsed()
    {
        List<string> warnings = [];
        List<FfbTelemetryDeviceOption> devices = MainWindowViewModel.ParseFfbTelemetryDevices(FakeFfbLauncher.DevicesJson, warnings);
        Assert.Equal(2, devices.Count);
        Assert.False(devices[0].HasForceAxes);
        Assert.True(devices[1].HasForceAxes);
        Assert.Equal("VPforce Rhino FFB Joystick", devices[1].Name);
        Assert.Contains("VID 0483 PID A1C0", devices[1].Label);
        Assert.Contains("force axes X Y", devices[1].Label);
        Assert.Equal(["one warning"], warnings);
    }

    internal const string FfbTelemetryOff = "off - telemetry force feedback is switched off";

    private static FfbTelemetryGameItem Game(MainWindowViewModel vm, string id) => vm.FfbTelemetryGames.Single(g => g.Id == id);

    private static string[] Tail(IReadOnlyList<string> args, int count) => args.Skip(args.Count - count).ToArray();

    private static string Status(TelemetryFfbState state, string reason, string? aircraft) =>
        HostProtocol.StatusLine(new TelemetryFfbStatus(state, reason, "DCS", aircraft, "Flight (built-in)", 1, "UDP 41992"), "Rhino");

    // The feature on, a game running, one poll: JoyPro.Ffb (fake) started for it.
    internal static FakeFfbHostProcess StartProcess(MainWindowViewModel vm, FakeFfbLauncher launcher, string game)
    {
        launcher.RunningGames.Add(game);
        vm.FfbTelemetryEnabled = true;
        vm.PollFfbTelemetryGamesOnce();
        Assert.True(vm.FfbTelemetryProcessRunning);
        return launcher.Started.Last();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out.");
    }

    internal static async Task<(MainWindowViewModel Vm, FfbTelemetryFakeEngine Engine, FakeFfbLauncher Launcher)> CreateLoadedVmAsync(
        UiStage stage = UiStage.FfbTelemetry, Action<FfbTelemetryFakeEngine>? configure = null)
    {
        FfbTelemetryFakeEngine engine = new();
        configure?.Invoke(engine);
        FakeFfbLauncher launcher = new();
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            FfbHostLauncher = launcher,
            FfbTelemetryDispatch = action => action(),
            FfbTelemetryPollInterval = TimeSpan.FromHours(1),
            FfbTelemetrySaveDelay = TimeSpan.FromHours(1),
            FfbTelemetryGuiPid = 777
        };
        vm.FfbFindGameProcess = launcher.Find;
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        await vm.RefreshFfbTelemetryStatusCommand.ExecuteAsync(null);
        // The first visit of the page loads the status again (once); then the stage the test wants.
        vm.CurrentStage = UiStage.FfbTelemetry;
        vm.CurrentStage = stage;
        return (vm, engine, launcher);
    }
}

// `engine ffb telemetry ...` as the engine answers it (PascalCase JSON, exit code 0 = ok).
[SupportedOSPlatform("windows")]
internal sealed class FfbTelemetryFakeEngine : IEngineCliClient
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public string Device { get; set; } = "{DEV-1}";

    public bool FailSetup { get; set; }

    public bool Enabled { get; set; }

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        (int code, string output) = Answer(arguments);
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = code,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = code == 0 ? string.Empty : "engine error: setup failed",
            Arguments = arguments.ToArray()
        });
    }

    private (int, string) Answer(IReadOnlyList<string> args)
    {
        if (Seq(args, "ffb", "telemetry", "status"))
            return (0, StatusJson());
        if (Seq(args, "ffb", "telemetry", "setup"))
        {
            bool remove = args.Contains("--remove");
            bool dryRun = args.Contains("--dry-run");
            if (FailSetup)
                return (1, "{\"Game\":\"IL2Game\",\"Action\":\"install\",\"DryRun\":false,\"Installed\":false,\"Changes\":[],\"Warnings\":[],\"Errors\":[\"IL-2 is running: close it first.\"],\"Files\":[]}");
            return (0, "{\"Game\":\"IL2Game\",\"Action\":\"" + (remove ? "remove" : "install") + "\",\"DryRun\":" + (dryRun ? "true" : "false") +
                       ",\"Installed\":" + (remove || dryRun ? "false" : "true") +
                       ",\"Changes\":[\"startup.cfg: addr2 = \\\"127.0.0.1:41994\\\"\"],\"Warnings\":[],\"Errors\":[],\"Files\":[\"C:\\\\IL-2\\\\data\\\\startup.cfg\"]}");
        }
        if (Seq(args, "ffb", "telemetry", "config"))
        {
            if (Seq(args, "config", "set", "--param", "enabled=true"))
                Enabled = true;
            if (Seq(args, "config", "set", "--param", "enabled=false"))
                Enabled = false;
            return (0, "{\"Saved\":true}");
        }
        return (0, "{}");
    }

    private string StatusJson()
    {
        string effects = string.Join(",", FfbEffectNames.All.Select(e => "{\"Name\":\"" + e.Name + "\",\"Title\":\"" + e.Title + "\"}"));
        return "{\"ConfigPath\":\"C:\\\\Users\\\\me\\\\AppData\\\\Local\\\\JoyPro\\\\Ffb\\\\Telemetry\\\\telemetry-ffb.json\"," +
               "\"Config\":{\"Enabled\":" + (Enabled ? "true" : "false") + ",\"Device\":\"" + Device + "\",\"DeviceName\":\"VPforce Rhino FFB Joystick\",\"Strength\":0.8,\"MaxForce\":0.6," +
               "\"InvertX\":false,\"InvertY\":true,\"TickRateHz\":50,\"TimeoutSeconds\":0.5,\"Games\":{" +
               "\"DCS\":{\"Enabled\":true,\"Port\":0,\"Graph\":\"viper.jpffb.json\",\"Strength\":1.2,\"Url\":\"\",\"PollHz\":20," +
               "\"Effects\":{\"buffet\":{\"Enabled\":false,\"Strength\":1},\"guns\":{\"Enabled\":true,\"Strength\":0.5}}," +
               "\"Aircraft\":{\"F-16C_50\":{\"Graph\":\"\",\"Strength\":0.8,\"Effects\":{}}}}," +
               "\"WarThunder\":{\"Enabled\":false,\"Port\":0,\"Graph\":\"\",\"Strength\":1,\"Url\":\"http://127.0.0.1:8111/\",\"PollHz\":20,\"Effects\":{},\"Aircraft\":{}}}}," +
               "\"Games\":[" +
               "{\"Game\":\"DCS\",\"Title\":\"DCS World\",\"ProcessNames\":[\"DCS\",\"DCS_mt\"],\"Running\":false,\"SetupNeeded\":true,\"SetupInstalled\":true,\"SetupDetail\":\"Export.lua has the JoyPro line.\",\"Endpoint\":\"UDP 41992\"}," +
               "{\"Game\":\"IL2Game\",\"Title\":\"IL-2 Great Battles\",\"ProcessNames\":[\"Il-2\"],\"Running\":false,\"SetupNeeded\":true,\"SetupInstalled\":false,\"SetupDetail\":\"\",\"Endpoint\":\"UDP 41994\"}," +
               "{\"Game\":\"IL2Korea\",\"Title\":\"IL-2 Series: Korea\",\"ProcessNames\":[\"IL2Series\"],\"Running\":false,\"SetupNeeded\":true,\"SetupInstalled\":false,\"SetupDetail\":\"\",\"Endpoint\":\"UDP 41994\"}," +
               "{\"Game\":\"WarThunder\",\"Title\":\"War Thunder\",\"ProcessNames\":[\"aces\",\"aces_be\"],\"Running\":false,\"SetupNeeded\":false,\"SetupInstalled\":false,\"SetupDetail\":\"\",\"Endpoint\":\"http://127.0.0.1:8111/\"}]," +
               "\"Effects\":[" + effects + "],\"Process\":{\"Running\":false,\"Pid\":0},\"LastStatus\":null}";
    }
}

// JoyPro.Ffb as the page sees it: started processes, the game lookup and the device list.
internal sealed class FakeFfbLauncher : IFfbHostLauncher
{
    internal const string DevicesJson =
        "{\"devices\":[" +
        "{\"instanceGuid\":\"{WHEEL}\",\"productGuid\":\"{P1}\",\"name\":\"Some Gamepad\",\"productName\":\"Some Gamepad\",\"vid\":\"046D\",\"pid\":\"C21D\",\"forceAxes\":[],\"effects\":[\"Rumble\"]}," +
        "{\"instanceGuid\":\"{RHINO}\",\"productGuid\":\"{P2}\",\"name\":\"VPforce Rhino FFB Joystick\",\"productName\":\"Rhino\",\"vid\":\"0483\",\"pid\":\"A1C0\",\"forceAxes\":[\"X\",\"Y\"],\"effects\":[\"ConstantForce\",\"Spring\"]}]," +
        "\"warnings\":[\"one warning\"]}";

    public List<FakeFfbHostProcess> Started { get; } = [];

    public List<IReadOnlyList<string>> ToolCalls { get; } = [];

    public HashSet<string> RunningGames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<IReadOnlyList<string>> LookedUp { get; } = [];

    public int Lookups => LookedUp.Count;

    public int GamePid { get; set; } = 4242;

    public int? Find(IReadOnlyList<string> names)
    {
        LookedUp.Add(names);
        return names.Any(RunningGames.Contains) ? GamePid : null;
    }

    public IFfbHostProcess Start(string exePath, IReadOnlyList<string> arguments)
    {
        FakeFfbHostProcess process = new(exePath, arguments.ToArray());
        Started.Add(process);
        return process;
    }

    public Task<FfbToolResult> RunToolAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ToolCalls.Add(arguments.ToArray());
        return Task.FromResult(new FfbToolResult(0, DevicesJson, string.Empty));
    }
}

internal sealed class FakeFfbHostProcess : IFfbHostProcess
{
    private bool exited;

    public FakeFfbHostProcess(string exePath, string[] arguments)
    {
        ExePath = exePath;
        Arguments = arguments;
    }

    public string ExePath { get; }

    public string[] Arguments { get; }

    public List<string> Sent { get; } = [];

    public bool IgnoreStop { get; set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public int Id => 9000;

    public bool HasExited => exited;

    public event Action<string>? LineReceived;

    public event Action<string>? ErrorLineReceived;

    public event Action? Exited;

    public void Emit(string line) => LineReceived?.Invoke(line);

    public void EmitError(string line) => ErrorLineReceived?.Invoke(line);

    public void Exit()
    {
        exited = true;
        Exited?.Invoke();
    }

    public bool Send(string command)
    {
        if (exited)
            return false;
        lock (Sent)
            Sent.Add(command);
        if (command == HostProtocol.Stop && !IgnoreStop)
            exited = true;
        return true;
    }

    public bool WaitForExit(int milliseconds) => exited;

    public void Kill()
    {
        Killed = true;
        exited = true;
    }

    public void Dispose() => Disposed = true;
}
