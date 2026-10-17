using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Virtual joystick mode (docs/virtual-joystick.md): the HidHide session state machine (engine/VJoy/HidHideSession.cs,
// linked into the GUI) against a fake HidHide CLI, and the page's view model against a fake engine that runs the same
// state machine. Nothing here touches the real HidHide, vJoy or JoyPro.VJoy.
[SupportedOSPlatform("windows")]
public sealed class VirtualJoystickTests : IDisposable
{
    private const string Stick = @"HID\VID_231D&PID_0125\c&8abe679&0&0000";
    private const string Throttle = @"HID\VID_03EB&PID_2054\c&7187b8&0&0000";
    private const string VJoy = @"HID\HIDCLASS\1&2d595ca7&1&0000";
    private const string UserDevice = @"HID\VID_16D0&PID_0A38\c&33bf0f2b&0&0000";
    private const string Dcs = @"D:\DCS World\bin\DCS.exe";
    private const string Msfs = @"H:\Steam\MicrosoftFlightSimulator\FlightSimulator.exe";
    private const string Forwarder = @"C:\JoyPro\vjoy\JoyPro.VJoy.exe";
    private const string Gremlin = @"C:\Tools\Gremlin\joystick_gremlin.exe";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vjoy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ the state machine

    [Fact]
    public void Enable_DormantUserSetup_ParksItHidesOnlyForTheGameAndRestoresExactly()
    {
        // This PC's real setup: cloaking off, inverse on, the user's own (dormant) entries incl. the vJoy device.
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy, UserDevice, Stick], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        HidHideResult enabled = session.Enable(Request(owner: 0), dryRun: false);

        Assert.True(enabled.Ok, enabled.Message + " " + string.Join(" ", enabled.Problems));
        Assert.Equal("block-list", enabled.Strategy);
        Assert.Single(cli.Calls); // one elevated call = one UAC prompt
        HidHideState during = cli.ReadState();
        Assert.True(during.CloakOn);
        Assert.True(during.InverseOn);
        Assert.Equal(new[] { Stick, Throttle }.OrderBy(d => d), during.Devices.OrderBy(d => d));
        Assert.Equal([Dcs], during.Apps);
        Assert.False(during.HasDevice(VJoy)); // the game must see vJoy
        Assert.False(during.HasApp(Msfs));     // the dormant entry would block MSFS once cloaking is on
        HidHideJournal journal = session.LoadJournal()!;
        Assert.Equal(HidHideJournal.PhaseActive, journal.Phase);
        Assert.True(journal.SetCloak);
        Assert.Contains(Msfs, journal.RemovedApps);
        Assert.Contains(VJoy, journal.RemovedDevices);
        Assert.DoesNotContain(Stick, journal.AddedDevices); // the user had it hidden already

        HidHideResult disabled = session.Disable(dryRun: false);

        Assert.True(disabled.Ok, disabled.Message);
        Assert.Equal(2, cli.Calls.Count);
        Assert.Equal("--cloak-off", cli.Calls[1][0]); // nothing stays hidden whatever happens after
        AssertSame(before, cli.ReadState());
        Assert.Null(session.LoadJournal());
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(folder, "history")));
    }

    [Fact]
    public void Enable_ActiveBlockList_KeepsTheUsersEntriesAndRemovesOnlyItsOwn()
    {
        HidHideState before = State(cloak: true, inverse: true, devices: [UserDevice], apps: [Msfs, Forwarder]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        HidHideResult enabled = session.Enable(Request(owner: 0), dryRun: false);

        Assert.True(enabled.Ok);
        HidHideState during = cli.ReadState();
        Assert.True(during.HasDevice(UserDevice));
        Assert.True(during.HasApp(Msfs));
        Assert.True(during.HasDevice(Stick) && during.HasDevice(Throttle));
        Assert.True(during.HasApp(Dcs));
        Assert.False(during.HasApp(Forwarder)); // on a block list the forwarder would lose the devices
        Assert.DoesNotContain("--cloak-on", cli.Calls[0]);

        // Meanwhile the user hides another device himself: it stays.
        HidHideState changed = cli.ReadState();
        changed.Devices.Add(@"HID\VID_AAAA&PID_0001\x&1&0&0000");
        cli.Write(changed);
        Assert.True(session.Disable(dryRun: false).Ok);

        HidHideState after = cli.ReadState();
        Assert.True(after.HasDevice(@"HID\VID_AAAA&PID_0001\x&1&0&0000"));
        after.Devices.Remove(@"HID\VID_AAAA&PID_0001\x&1&0&0000");
        AssertSame(before, after);
    }

    [Fact]
    public void Enable_UsersActiveAllowList_StaysAnAllowListWithTheForwarderAllowed()
    {
        HidHideState before = State(cloak: true, inverse: false, devices: [UserDevice, VJoy], apps: [Gremlin, Dcs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        HidHideResult enabled = session.Enable(Request(owner: 0), dryRun: false);

        Assert.True(enabled.Ok);
        Assert.Equal("allow-list", enabled.Strategy);
        HidHideState during = cli.ReadState();
        Assert.False(during.InverseOn);
        Assert.True(during.HasApp(Forwarder));
        Assert.True(during.HasApp(Gremlin));
        Assert.False(during.HasApp(Dcs)); // allowed, DCS would still see the physical devices
        Assert.False(during.HasDevice(VJoy));
        Assert.Contains(enabled.Notes, n => n.Contains("allow list", StringComparison.Ordinal));

        Assert.True(session.Disable(dryRun: false).Ok);
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void Enable_NothingInUse_SwitchesToInverseModeAndBack()
    {
        HidHideState before = State(cloak: false, inverse: false, devices: [], apps: [@"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideClient.exe"]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        Assert.True(session.Enable(Request(owner: 0), dryRun: false).Ok);
        Assert.True(cli.ReadState().InverseOn);
        Assert.True(session.LoadJournal()!.SetInverse);

        Assert.True(session.Disable(dryRun: false).Ok);
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void DryRun_ChangesNothingAndListsTheCommands()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        HidHideResult result = session.Enable(Request(owner: 0), dryRun: true);

        Assert.True(result.Ok);
        Assert.Empty(cli.Calls);
        Assert.Null(session.LoadJournal());
        Assert.Contains("--dev-hide \"" + Stick + "\"", result.Commands);
        Assert.Contains("--app-reg \"" + Dcs + "\"", result.Commands);
        Assert.Equal("--cloak-on", result.Commands.Last());
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void Enable_DeclinedPrompt_LeavesNothingBehind()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        cli.ApplyExitCode = HidHideCli.Cancelled;

        HidHideResult result = session.Enable(Request(owner: 0), dryRun: false);

        Assert.False(result.Ok);
        Assert.True(result.Cancelled);
        Assert.Null(session.LoadJournal());
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void CrashedOwner_IsStaleAndRecoverRestoresThePreviousState()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy, UserDevice], apps: [Msfs]);
        bool ownerAlive = true;
        (FakeHidHideCli cli, HidHideSession session) = Session(before, (_, _) => ownerAlive);
        Assert.True(session.Enable(Request(owner: 4711), dryRun: false).Ok);

        // While the GUI that started it runs, nothing happens.
        HidHideResult early = session.Recover(dryRun: false, force: false);
        Assert.True(early.Ok);
        Assert.Equal(HidHideJournal.PhaseActive, early.SessionPhase);
        Assert.True(cli.ReadState().CloakOn);

        // The GUI crashed: the next start finds the session stale, a new enable is refused, recover restores.
        ownerAlive = false;
        HidHideSession next = new(cli, Path.Combine(folder, "hidhide-session.txt"), (_, _) => ownerAlive);
        Assert.True(next.IsStale(next.LoadJournal()));
        Assert.False(next.Enable(Request(owner: 99), dryRun: false).Ok);
        HidHideResult recovered = next.Recover(dryRun: false, force: false);
        Assert.True(recovered.Ok, recovered.Message);
        AssertSame(before, cli.ReadState());
        Assert.Null(next.LoadJournal());
    }

    [Fact]
    public void CrashWhileApplying_IsRepaired()
    {
        HidHideState before = State(cloak: true, inverse: true, devices: [UserDevice], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before, (_, _) => false);
        Assert.True(session.Enable(Request(owner: 0), dryRun: false).Ok);
        // As if JoyPro died right after the journal was written and HidHide took half of the change.
        string journalPath = Path.Combine(folder, "hidhide-session.txt");
        HidHideJournal journal = HidHideJournal.Parse(File.ReadAllText(journalPath))!;
        journal.Phase = HidHideJournal.PhaseApplying;
        journal.CreatedUtc = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllText(journalPath, journal.Serialize());
        HidHideState half = cli.ReadState();
        half.Apps.Remove(Dcs);
        cli.Write(half);

        Assert.True(session.IsStale(session.LoadJournal()));
        Assert.True(session.Recover(dryRun: false, force: false).Ok);
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void SecondGame_ExtendsTheSessionAndOneRestoreUndoesAll()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [], apps: []);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        Assert.True(session.Enable(Request(owner: 0), dryRun: false).Ok);
        HidHideRequest second = Request(owner: 0);
        second.Games = ["IL2Game"];
        second.GameExes = [@"D:\IL-2\bin\game\Il-2.exe"];
        second.HideDevices = [Stick, @"HID\VID_3344&PID_80CB&Col01\c&2eaeded2&0&0000"];

        HidHideResult extended = session.Enable(second, dryRun: false);

        Assert.True(extended.Ok);
        HidHideJournal journal = session.LoadJournal()!;
        Assert.Equal(["DCS", "IL2Game"], journal.Games);
        Assert.False(journal.Previous.CloakOn); // still the state before the first change
        Assert.Equal(3, journal.AddedDevices.Count);
        Assert.True(session.Disable(dryRun: false).Ok);
        AssertSame(before, cli.ReadState());
    }

    [Theory]
    [InlineData(HidHideState.PartCloak)]
    [InlineData(HidHideState.PartInverse)]
    [InlineData(HidHideState.PartDevices)]
    [InlineData(HidHideState.PartApps)]
    public void Enable_UnreadablePart_ChangesNothingAndWritesNoJournal(string part)
    {
        // The live test: HidHideCLI hung or was denied; the old code took the failed read as "off".
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy, UserDevice], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        cli.Hanging.Add(part);

        HidHideResult result = session.Enable(Request(owner: 0), dryRun: false);

        Assert.False(result.Ok);
        Assert.Contains("could not be read", result.Message);
        Assert.Contains("Nothing was changed", result.Message);
        Assert.Empty(cli.Calls);
        Assert.Null(session.LoadJournal());
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void DryRun_DeniedDeviceList_SaysItIsPartial()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy, UserDevice], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        cli.Denied.Add(HidHideState.PartDevices);

        HidHideResult result = session.Enable(Request(owner: 0), dryRun: true);

        Assert.True(result.Ok);
        Assert.Contains("partial", result.Message);
        Assert.Contains(result.Notes, n => n.Contains("hidden devices could not be read", StringComparison.Ordinal) && n.Contains("Access is denied", StringComparison.Ordinal));
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public void Disable_UnreadablePart_KeepsTheSessionAndChangesNothing()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy], apps: [Msfs]);
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        Assert.True(session.Enable(Request(owner: 0), dryRun: false).Ok);
        HidHideState during = cli.ReadState();
        cli.Hanging.Add(HidHideState.PartInverse);

        HidHideResult result = session.Disable(dryRun: false);

        Assert.False(result.Ok);
        Assert.NotNull(session.LoadJournal());
        AssertSame(during, cli.ReadState());
        cli.Hanging.Clear();
        Assert.True(session.Disable(dryRun: false).Ok);
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void Restore_UnknownPreviousInverseMode_IsLeftAlone()
    {
        // A journal whose "previous" lacks the inverse line (as the faulty build could have written): never set it off.
        HidHideJournal journal = HidHideJournal.Parse("# JoyPro HidHide session v1\nphase=active\nset-cloak=true\nset-inverse=true\nadded-app=" + Dcs + "\nprevious=--cloak-off\n")!;
        Assert.True(journal.Previous.InverseUnknown);
        Assert.False(journal.Previous.CloakUnknown);

        HidHidePlan plan = HidHidePlanner.PlanRestore(journal, State(cloak: true, inverse: true, devices: [], apps: [Dcs]));

        Assert.DoesNotContain("--inv-off", plan.Arguments);
        Assert.DoesNotContain("--inv-on", plan.Arguments);
        Assert.Contains("--cloak-off", plan.Arguments);
        Assert.Contains(plan.Notes, n => n.Contains("inverse mode before the session is not known", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanApply_RefusesAnIncompleteState()
    {
        HidHideState partial = State(cloak: false, inverse: true, devices: [], apps: []);
        partial.SetUnknown(HidHideState.PartDevices);

        HidHidePlan plan = HidHidePlanner.PlanApply(partial, Request(owner: 0), null);

        Assert.False(plan.Ok);
        Assert.Empty(plan.Arguments);
    }

    [Theory]
    [InlineData(HidHideState.PartCloak, "--cloak-on", true)]
    [InlineData(HidHideState.PartInverse, "--inv-on", true)]
    [InlineData(HidHideState.PartApps, "--app-reg \"C:\\x.exe\"", true)]
    [InlineData(HidHideState.PartApps, "", true)]
    [InlineData(HidHideState.PartDevices, "Error code 0x0005 - Access is denied.", false)]
    [InlineData(HidHideState.PartCloak, "", false)]
    [InlineData(HidHideState.PartInverse, "Zugriff verweigert", false)]
    [InlineData(HidHideState.PartDevices, "something else", false)]
    public void TryParsePart_TakesOnlyRealAnswers(string part, string output, bool ok)
    {
        HidHideState state = new();

        bool parsed = HidHideState.TryParsePart(part, output, state, out string problem);

        Assert.Equal(ok, parsed);
        Assert.Equal(!ok, !state.Complete);
        Assert.Equal(ok, problem.Length == 0);
    }

    [Fact]
    public void Journal_RoundTrips()
    {
        HidHideJournal journal = new()
        {
            Phase = HidHideJournal.PhaseActive,
            OwnerPid = 12,
            OwnerStartTicks = 638000000000000000,
            Strategy = HidHideStrategy.AllowList,
            Games = ["DCS", "MSFS"],
            Previous = State(cloak: true, inverse: false, devices: [UserDevice], apps: [Gremlin]),
            SetCloak = true,
            SetInverse = true,
            AddedDevices = [Stick],
            AddedApps = [Forwarder],
            RemovedDevices = [VJoy],
            RemovedApps = [Dcs]
        };

        HidHideJournal parsed = HidHideJournal.Parse(journal.Serialize())!;

        Assert.Equal(journal.Serialize(), parsed.Serialize());
        Assert.Null(HidHideJournal.Parse("not a journal"));
    }

    [Fact]
    public void Parse_ReadsHidHideCliOutput()
    {
        HidHideState state = HidHideState.Parse("--cloak-off\r\n--inv-on\r\n--dev-hide \"" + VJoy + "\"\r\n--app-reg \"" + Msfs + "\"\r\n");

        Assert.False(state.CloakOn);
        Assert.True(state.InverseOn);
        Assert.Equal([VJoy], state.Devices);
        Assert.Equal([Msfs], state.Apps);
    }

    // ------------------------------------------------------------------ the page

    [Fact]
    public async Task Status_FillsGamesSlotsNeedsAndSession()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();

        Assert.True(vm.VJoyInstalled);
        Assert.True(vm.HidHideInstalled);
        Assert.Contains("2.1.9.1", vm.VJoyStatusText);
        Assert.Equal(["DCS", "IL2Game", "HLLVietnam"], vm.VirtualJoystickGames.Select(g => g.Id).ToList());
        Assert.True(vm.VirtualJoystickGames[0].IsVirtual);
        Assert.False(vm.VirtualJoystickGames[1].IsVirtual);
        Assert.Equal(2, vm.VirtualJoystickSlots.Count);
        Assert.Equal("VKB Stick  JOY_X", vm.VirtualJoystickSlots[0].PhysicalText);
        Assert.Equal("vJoy 1  JOY_X", vm.VirtualJoystickSlots[0].VirtualText);
        Assert.False(vm.VirtualJoystickNeedsConfig);
        Assert.Equal(144, vm.VirtualJoystickRefreshRate);
        Assert.True(vm.CanStartVirtualJoystick);
        Assert.Contains(engine.Calls, c => c.Contains("vjoy") && c.Contains("status"));
    }

    [Fact]
    public async Task Status_DeniedDeviceList_IsShownAsUnknown()
    {
        VJoyFakeEngine engine = new(folder) { DeviceListDenied = true };
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);

        Assert.Contains("hidden devices: only readable with administrator rights", vm.HidHideStatusText);
        Assert.DoesNotContain("0 hidden device", vm.HidHideStatusText);
    }
    [Fact]
    public async Task XboxGame_StatusShowsTheXboxTableWhatDoesNotFitAndViGEm()
    {
        VJoyFakeEngine engine = new(folder);
        engine.Modes["HLLVietnam"] = "xbox";
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);

        VirtualJoystickGameItem hll = vm.VirtualJoystickGames.Single(g => g.Id == "HLLVietnam");
        Assert.True(hll.IsXbox);
        Assert.Equal(2, hll.ModeIndex);
        Assert.Contains("game's own gamepad layout (9 actions known)", hll.XboxNote);
        Assert.True(vm.HasXboxGames);
        VirtualJoystickSlotItem roll = vm.VirtualJoystickSlots.Single(s => s.IsXbox && s.Input == "JOY_X");
        Assert.Equal("Xbox pad  Left stick X (game layout)  [HLLVietnam]", roll.VirtualText);
        Assert.Contains("does not fit: VKB Stick JOY_BTN30", vm.VirtualJoystickNeedsText);
        Assert.True(vm.ViGEmInstalled);
        Assert.Contains("ViGEmBus 1.22.0 is installed", vm.ViGEmStatusText);
    }

    [Fact]
    public async Task OneDeviceGame_SuggestsItsVirtualMode()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();
        VirtualJoystickGameItem hll = vm.VirtualJoystickGames.Single(g => g.Id == "HLLVietnam");
        VirtualJoystickGameItem il2 = vm.VirtualJoystickGames.Single(g => g.Id == "IL2Game");

        Assert.False(il2.ShowSuggestion);
        Assert.True(hll.ShowSuggestion);
        Assert.Contains("One-device game", hll.SuggestionText);
        Assert.Contains("Suggested: Xbox pad", hll.SuggestionText);

        hll.ApplySuggestionCommand.Execute(null);
        await WaitUntilAsync(() => engine.Calls.Any(c => Seq(c, "vjoy", "mode", "HLLVietnam", "xbox")));
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.False(hll.ShowSuggestion);
    }

    [Fact]
    public async Task XboxGame_ModeSwitchStartAndLiveView()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        VirtualJoystickGameItem hll = vm.VirtualJoystickGames.Single(g => g.Id == "HLLVietnam");

        hll.ModeIndex = 2;
        await WaitUntilAsync(() => engine.Calls.Any(c => Seq(c, "vjoy", "mode", "HLLVietnam", "xbox")));
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Contains("virtual Xbox 360 pad", vm.VirtualJoystickMessage);

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        FakeFfbHostProcess host = Assert.Single(launcher.Started);
        Assert.True(Seq(host.Arguments, "--xbox-sink", "dry"));
        host.Emit("xout -32768 32767 0 0 255 0 1001");
        VirtualJoystickLiveDevice pad = vm.VirtualJoystickLive.Single(d => d.Id == 0);
        Assert.Equal("Virtual Xbox 360 pad", pad.Title);
        Assert.Equal(0, pad.Axes[0].Value);
        Assert.Equal(100, pad.Axes[1].Value);
        Assert.Equal(100, pad.Axes[4].Value);
        Assert.Equal("A, D-pad up", pad.ButtonsText);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.True(Seq(launcher.Started.Last().Arguments, "--xbox-sink", "vigem"));
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task XboxGame_WithoutViGEm_StartIsRefusedTestStillWorks()
    {
        VJoyFakeEngine engine = new(folder) { ViGEmInstalled = false };
        engine.Modes["HLLVietnam"] = "xbox";
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateVmAsync(engine);
        Assert.False(vm.ViGEmInstalled);
        Assert.Contains("not installed", vm.ViGEmStatusText);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Empty(launcher.Started);
        Assert.Contains("ViGEmBus driver is not installed", vm.VirtualJoystickMessage);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("apply-hidhide"));

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Single(launcher.Started);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
    }
    [Fact]
    public async Task ToggleGame_RunsVjoyModeAndRefreshes()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();
        VirtualJoystickGameItem il2 = vm.VirtualJoystickGames.Single(g => g.Id == "IL2Game");

        il2.ModeIndex = 1;
        await WaitUntilAsync(() => engine.Calls.Any(c => Seq(c, "vjoy", "mode", "IL2Game", "virtual")));
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Contains("IL-2", vm.VirtualJoystickMessage);
        Assert.Contains(engine.Calls, c => Seq(c, "vjoy", "mode", "IL2Game", "virtual") && c.Contains("--profile"));
    }

    [Fact]
    public async Task Test_StartsTheDryForwarderWithoutHidHideAndShowsTheLiveView()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);

        FakeFfbHostProcess host = Assert.Single(launcher.Started);
        Assert.Equal(["run", "--config", Path.Combine(vm.VirtualJoystickDataFolder, "forwarder.json"), "--sink", "dry", "--parent-pid", "777", "--protocol", "--monitor"], host.Arguments);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("apply-hidhide"));
        Assert.True(vm.VirtualJoystickRunning);
        Assert.True(vm.VirtualJoystickTestMode);

        host.Emit("ready sink=dry rate=144 period-ms=6 devices=1 sources=2/2 slots=2 axis-range=0..32767");
        host.Emit("out 1 0 32767 16384 16384 16384 16384 16384 16384 00000000000000000000000080000005 9000 -1 -1 -1");
        Assert.Contains("2/2 physical device(s) connected", vm.VirtualJoystickForwarderText, StringComparison.Ordinal);
        VirtualJoystickLiveDevice live = Assert.Single(vm.VirtualJoystickLive);
        Assert.Equal(0, live.Axes[0].Value);
        Assert.Equal(100, live.Axes[1].Value);
        Assert.Equal("1, 3, 32", live.ButtonsText);
        Assert.Equal("hat 1: 90°", live.HatsText);

        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Contains("stop", host.Sent);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("restore-hidhide"));
        Assert.False(vm.VirtualJoystickRunning);
    }

    [Fact]
    public async Task Start_HidesWithOnePromptForwardsAndStopRestoresHidHide()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);

        IReadOnlyList<string> apply = engine.Calls.Single(c => c.Contains("apply-hidhide"));
        Assert.True(Seq(apply, "--owner-pid", "777"));
        Assert.Contains(vm.VirtualJoystickExePath, apply);
        Assert.Single(engine.Cli.Calls);
        Assert.True(engine.Cli.ReadState().CloakOn);
        FakeFfbHostProcess host = Assert.Single(launcher.Started);
        Assert.True(Seq(host.Arguments, "--sink", "vjoy"));
        // The order: mapping, table, HidHide, then the forwarder.
        List<string> steps = engine.Calls.Where(c => c.Contains("vjoy")).Select(c => c[c.ToList().IndexOf("vjoy") + 1]).ToList();
        Assert.True(steps.IndexOf("map") < steps.IndexOf("forwarder-config") && steps.IndexOf("forwarder-config") < steps.IndexOf("apply-hidhide"));

        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Contains("stop", host.Sent);
        Assert.Contains(engine.Calls, c => c.Contains("restore-hidhide"));
        AssertSame(before, engine.Cli.ReadState());
        Assert.False(vm.VirtualJoystickRunning);
    }

    [Fact]
    public async Task Start_DeclinedPrompt_StartsNothing()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        engine.Cli.ApplyExitCode = HidHideCli.Cancelled;

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Empty(launcher.Started);
        Assert.False(vm.VirtualJoystickRunning);
        Assert.Contains("declined", vm.VirtualJoystickMessage);
        Assert.False(engine.Cli.ReadState().CloakOn);
    }

    [Fact]
    public async Task ForwarderEndsByItself_HidHideIsRestored()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        FakeFfbHostProcess host = launcher.Started.Single();

        host.Emit("error vJoy device 1 is used by another program (a feeder such as Joystick Gremlin or JoyPro's test kit).");
        host.Exit();
        await WaitUntilAsync(() => engine.Calls.Any(c => c.Contains("restore-hidhide")) && !vm.IsBusy);

        Assert.False(vm.VirtualJoystickRunning);
        AssertSame(before, engine.Cli.ReadState());
        Assert.Contains("another program", vm.VirtualJoystickMessage);
    }

    [Fact]
    public async Task StartupAfterCrash_RecoversTheStaleSession()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.True(engine.Cli.ReadState().CloakOn);

        // The next JoyPro start: the owner (pid 777) is gone.
        (MainWindowViewModel next, _, _) = await CreateVmAsync(engine, refresh: false);
        engine.OwnerAlive = false;
        next.VirtualJoystickOwnerAlive = (_, _) => false;

        bool recovered = await next.RecoverStaleVirtualJoystickSessionAsync();

        Assert.True(recovered);
        Assert.Contains(engine.Calls, c => Seq(c, "vjoy", "recover"));
        AssertSame(before, engine.Cli.ReadState());
        Assert.False(next.VirtualJoystickSessionStale);
    }

    [Fact]
    public async Task StartupWithLiveOwner_DoesNothing()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        (MainWindowViewModel second, _, _) = await CreateVmAsync(engine, refresh: false);
        second.VirtualJoystickOwnerAlive = (_, _) => true;

        Assert.False(await second.RecoverStaleVirtualJoystickSessionAsync());
        Assert.DoesNotContain(engine.Calls, c => Seq(c, "vjoy", "recover"));
        Assert.True(engine.Cli.ReadState().CloakOn);
    }

    [Fact]
    public async Task AutoStart_FollowsTheGameProcess()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();
        vm.VirtualJoystickPollInterval = TimeSpan.FromHours(1);
        vm.VirtualJoystickAutoStart = true;
        Assert.True(vm.VirtualJoystickPolling);
        int? running = null;
        vm.VirtualJoystickFindGame = names => names.Contains("DCS") ? running : null;
        vm.VirtualJoystickProcessPath = _ => @"D:\DCS World\bin-mt\DCS.exe";

        await vm.CheckVirtualJoystickGamesOnceAsync();
        Assert.Empty(launcher.Started);

        running = 5150;
        await vm.CheckVirtualJoystickGamesOnceAsync();
        Assert.Single(launcher.Started);
        Assert.Contains(engine.Calls, c => c.Contains("apply-hidhide") && Seq(c, "--exe", @"D:\DCS World\bin-mt\DCS.exe"));

        running = null;
        await vm.CheckVirtualJoystickGamesOnceAsync();
        Assert.False(vm.VirtualJoystickRunning);
        AssertSame(before, engine.Cli.ReadState());
        Assert.Contains("game ended", vm.VirtualJoystickMessage);

        vm.VirtualJoystickAutoStart = false;
        Assert.False(vm.VirtualJoystickPolling);
    }

    [Fact]
    public async Task Shutdown_StopsTheForwarderAndRestoresThroughADetachedEngine()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        List<System.Diagnostics.ProcessStartInfo> started = [];
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> previous = ExternalProcess.Start;
        ExternalProcess.Start = info =>
        {
            started.Add(info);
            return null;
        };
        try
        {
            vm.StopVirtualJoystickForShutdown();
        }
        finally
        {
            ExternalProcess.Start = previous;
        }

        Assert.Contains("stop", launcher.Started.Single().Sent);
        System.Diagnostics.ProcessStartInfo restore = Assert.Single(started);
        Assert.Equal(["vjoy", "restore-hidhide"], restore.ArgumentList.ToList());
    }

    // ------------------------------------------------------------------ helpers

    private (FakeHidHideCli Cli, HidHideSession Session) Session(HidHideState start, Func<int, long, bool>? alive = null)
    {
        FakeHidHideCli cli = new(Path.Combine(folder, "hidhide-state.txt"));
        cli.Write(start);
        return (cli, new HidHideSession(cli, Path.Combine(folder, "hidhide-session.txt"), alive ?? ((_, _) => true)));
    }

    internal static HidHideRequest Request(int owner) => new()
    {
        HideDevices = [Stick, Throttle],
        KeepVisible = [VJoy],
        GameExes = [Dcs],
        TrustedExes = [Forwarder],
        Games = ["DCS"],
        OwnerPid = owner,
        OwnerStartTicks = owner == 0 ? 0 : 1
    };

    internal static HidHideState State(bool cloak, bool inverse, List<string> devices, List<string> apps) =>
        new() { CloakOn = cloak, InverseOn = inverse, Devices = devices, Apps = apps };

    internal static void AssertSame(HidHideState expected, HidHideState actual)
    {
        Assert.Equal(expected.CloakOn, actual.CloakOn);
        Assert.Equal(expected.InverseOn, actual.InverseOn);
        Assert.Equal(expected.Devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase), actual.Devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase), actual.Apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(MainWindowViewModel Vm, VJoyFakeEngine Engine, FakeFfbLauncher Launcher)> CreateVmAsync(VJoyFakeEngine? engine = null, bool refresh = true)
    {
        engine ??= new VJoyFakeEngine(folder);
        FakeFfbLauncher launcher = new();
        string exe = Path.Combine(folder, "vjoy", "JoyPro.VJoy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty); // only its existence is checked; the fake launcher starts nothing
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            VirtualJoystickLauncher = launcher,
            VirtualJoystickDispatch = action => action(),
            VirtualJoystickPollInterval = TimeSpan.FromHours(1),
            VirtualJoystickGuiPid = 777,
            VirtualJoystickGuiExe = @"C:\JoyPro\JoyPro.Gui.exe",
            VirtualJoystickExePath = exe,
            VirtualJoystickDataFolder = Path.Combine(folder, "data"),
            VirtualJoystickFindGame = _ => null,
            VirtualJoystickProcessPath = _ => null
        };
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        vm.ProfilePath = Path.Combine(folder, "vjoy.pr0file");
        engine.Session = new HidHideSession(engine.Cli, Path.Combine(folder, "data", "hidhide-session.txt"), (_, _) => engine.OwnerAlive);
        if (refresh)
            await vm.RefreshVirtualJoystickCommand.ExecuteAsync(null);
        return (vm, engine, launcher);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out.");
    }

    internal static bool Seq(IReadOnlyList<string> args, params string[] sequence)
    {
        for (int i = 0; i + sequence.Length <= args.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length && match; j++)
                match = string.Equals(args[i + j], sequence[j], StringComparison.OrdinalIgnoreCase);
            if (match)
                return true;
        }

        return false;
    }
}

// `engine vjoy ...` as the engine answers it, with HidHide run by the real session state machine against a fake CLI
// (the HidHide state the user had on this PC: cloaking off, inverse on, dormant own entries).
[SupportedOSPlatform("windows")]
internal sealed class VJoyFakeEngine : IEngineCliClient
{
    public VJoyFakeEngine(string folder)
    {
        Cli = new FakeHidHideCli(Path.Combine(folder, "fake-hidhide-state.txt"));
        Cli.Write(VirtualJoystickTests.State(cloak: false, inverse: true, devices: [@"HID\HIDCLASS\1&2d595ca7&1&0000"], apps: [@"H:\Steam\MicrosoftFlightSimulator\FlightSimulator.exe"]));
        Session = new HidHideSession(Cli, Path.Combine(folder, "data", "hidhide-session.txt"), (_, _) => OwnerAlive);
    }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public FakeHidHideCli Cli { get; }

    public HidHideSession Session { get; set; }

    public bool OwnerAlive { get; set; } = true;

    // Without administrator rights HidHide denies its hidden-device list (status shows it as unknown).
    public bool DeviceListDenied { get; set; }

    public Dictionary<string, string> Modes { get; } = new(StringComparer.OrdinalIgnoreCase) { ["DCS"] = "virtual", ["IL2Game"] = "native", ["HLLVietnam"] = "native" };

    public bool ViGEmInstalled { get; set; } = true;

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        (int code, string output) = Answer(arguments.ToList());
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = code,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = code == 0 ? string.Empty : "vjoy failed",
            Arguments = arguments.ToArray()
        });
    }

    private (int, string) Answer(List<string> args)
    {
        int at = args.IndexOf("vjoy");
        if (at < 0 || at + 1 >= args.Count)
            return (0, "{}");
        string action = args[at + 1];
        switch (action)
        {
            case "status":
                return (0, Status());
            case "mode":
                Modes[args[at + 2]] = args[at + 3];
                return (0, Json(new { Message = "ok" }));
            case "map":
            case "settings":
                return (0, Json(new { Message = "The mapping is up to date." }));
            case "forwarder-config":
                string path = args[args.IndexOf("--out") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{}");
                return (0, Json(new { Message = "Wrote the forwarder table", Path = path }));
            case "apply-hidhide":
                HidHideRequest request = VirtualJoystickTests.Request(int.Parse(args[args.IndexOf("--owner-pid") + 1]));
                int exe = args.IndexOf("--exe");
                if (exe >= 0)
                    request.GameExes.Add(args[exe + 1]);
                return Result(Session.Enable(request, dryRun: false));
            case "restore-hidhide":
                return Result(Session.Disable(dryRun: false));
            case "recover":
                return Result(Session.Recover(dryRun: false, force: false));
            default:
                return (0, "{}");
        }
    }

    private static (int, string) Result(HidHideResult result) =>
        (result.Cancelled ? 2 : result.Ok ? 0 : 1, Json(new { result.Ok, result.Cancelled, result.Message, result.Strategy, result.Commands, result.SessionPhase }));

    private string Status()
    {
        HidHideState state = Cli.ReadState();
        HidHideJournal? journal = Session.LoadJournal();
        object status = new
        {
            VJoy = new
            {
                Installed = true,
                Version = "2.1.9.1",
                Fake = false,
                Devices = new[] { new { Id = 1, Configured = true, Describe = "x,y,z,rx,ry,rz,sl0,sl1, 32 buttons, 4 hats", DirectInputName = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}" } }
            },
            HidHide = new { Installed = true, Version = "1.5.230.0", state.CloakOn, state.InverseOn, state.Devices, state.Apps, Unreadable = DeviceListDenied ? new[] { "hidden devices" } : Array.Empty<string>() },
            Session = new
            {
                Phase = journal == null ? "none" : journal.Phase,
                Stale = journal != null && Session.IsStale(journal),
                Strategy = journal == null ? "" : "block-list",
                Games = journal == null ? [] : journal.Games,
                AddedDevices = journal == null ? [] : journal.AddedDevices
            },
            Settings = new { FirstDevice = 1, LastDevice = 16, AutoStart = false, HideDevices = true },
            Games = new object[]
            {
                new { Id = "DCS", Name = "DCS World", Mode = Modes["DCS"], Relations = 12, ProcessNames = new[] { "DCS" }, Exes = new[] { @"D:\DCS World\bin\DCS.exe" }, GamepadLayout = 0, WritesGamepadBinds = false },
                new { Id = "IL2Game", Name = "IL-2 Sturmovik: Great Battles", Mode = Modes["IL2Game"], Relations = 3, ProcessNames = new[] { "Il-2" }, Exes = Array.Empty<string>(), GamepadLayout = 0, WritesGamepadBinds = false },
                new { Id = "HLLVietnam", Name = "Hell Let Loose: Vietnam", Mode = Modes["HLLVietnam"], Relations = 4, ProcessNames = new[] { "HLLVietnam-Win64-Shipping" }, Exes = new[] { @"H:\HLLV\HLLVietnam-Win64-Shipping.exe" }, GamepadLayout = 9, WritesGamepadBinds = false, SuggestedMode = "xbox", MaxJoysticks = 0, DeviceLimitNote = "Reads no joystick, only Xbox controllers (XInput): fly it with the virtual Xbox pad." },
                new { Id = "MSFS", Name = "Microsoft Flight Simulator", Mode = "native", Relations = 0, ProcessNames = new[] { "FlightSimulator" }, Exes = Array.Empty<string>() }
            },
            Mapping = new object[]
            {
                new { Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_X", Device = 1, Target = "JOY_X", Connected = true, Used = true, New = false, Relations = new[] { "Roll" } },
                new { Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_BTN1", Device = 1, Target = "JOY_BTN1", Connected = true, Used = true, New = false, Relations = new[] { "Fire" } }
            },
            ViGEm = new { Installed = ViGEmInstalled, Running = ViGEmInstalled, Version = ViGEmInstalled ? "1.22.0" : "", Fake = false, InstallerUrl = "https://github.com/nefarius/ViGEmBus/releases" },
            XboxMapping = Modes["HLLVietnam"] != "xbox" ? Array.Empty<object>() : new object[]
            {
                new { Game = "HLLVietnam", Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_X", Target = "XB_LX", TargetName = "Left stick X", Pinned = true, Action = "Driver_RollRight", Connected = true, Used = true, New = false, Relations = new[] { "Roll" } },
                new { Game = "HLLVietnam", Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_BTN1", Target = "XB_A", TargetName = "A", Pinned = false, Action = "", Connected = true, Used = true, New = false, Relations = new[] { "Flare" } }
            },
            XboxGames = Modes["HLLVietnam"] != "xbox" ? Array.Empty<object>() : new object[] { new { Game = "HLLVietnam", Slots = 2, Unfit = new[] { "VKB Stick JOY_BTN30 (Gear) does not fit: every Xbox button is taken." }, Notes = Array.Empty<string>() } },
            Needs = new[] { new { Id = 1, Slots = 2, Sufficient = true, Missing = Array.Empty<string>(), Current = "x,y,z,rx,ry,rz,sl0,sl1, 32 buttons, 4 hats", Recommended = "same", ConfigCommand = "" } },
            RefreshRateHz = 144,
            Errors = Array.Empty<string>(),
            Warnings = Array.Empty<string>()
        };
        return Json(status);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
