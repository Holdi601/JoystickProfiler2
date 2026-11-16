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
    [InlineData(HidHideState.PartApps, "--app-reg \"\"\r\n--app-reg \"\"\r\n", true)] // entries HidHide cannot name (this PC, 2026-11-01)
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

    // HidHideCLI prints program entries whose stored path is on a volume that is not mounted any more as --app-reg ""
    // (the development PC after its volume numbers changed, 2026-11-01): five such entries made the program list
    // "unknown", so no session could start. They are a complete answer: left as they are, their number checked.
    [Fact]
    public void Enable_ProgramEntriesHidHideCannotName_AreLeftAsTheyAreAndRestoredExactly()
    {
        HidHideState parsed = new();
        Assert.True(HidHideState.TryParsePart(HidHideState.PartApps, "--app-reg \"\"\r\n--app-reg \"" + Msfs + "\"\r\n--app-reg \"\"\r\n", parsed, out string problem), problem);
        Assert.Equal(2, parsed.UnnamedApps);
        Assert.Equal([Msfs], parsed.Apps);

        HidHideState before = State(cloak: false, inverse: true, devices: [UserDevice], apps: [Msfs]);
        before.UnnamedApps = 5;
        (FakeHidHideCli cli, HidHideSession session) = Session(before);

        HidHideResult enabled = session.Enable(Request(owner: 0), dryRun: false);

        Assert.True(enabled.Ok, enabled.Message + " " + string.Join(" ", enabled.Problems));
        Assert.Contains(enabled.Notes, n => n.Contains("cannot name", StringComparison.Ordinal));
        HidHideState during = cli.ReadState();
        Assert.Equal(5, during.UnnamedApps);
        Assert.Equal([Dcs], during.Apps);
        Assert.DoesNotContain(cli.Calls[0], a => a.Length == 0); // never --app-unreg "" / --app-reg ""

        Assert.True(session.Disable(dryRun: false).Ok);

        HidHideState after = cli.ReadState();
        AssertSame(before, after);
        Assert.Equal(5, after.UnnamedApps);
        Assert.Empty(HidHidePlanner.Verify(before, after));
        Assert.Contains(HidHidePlanner.Verify(new HidHideState { CloakOn = after.CloakOn, InverseOn = after.InverseOn, Devices = after.Devices, Apps = after.Apps }, after),
            p => p.Contains("cannot name", StringComparison.Ordinal));
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
        // Alphabetical by the name shown: DCS World, Hell Let Loose: Vietnam, IL-2 Sturmovik (the engine sends them in its own order).
        Assert.Equal(["DCS", "HLLVietnam", "IL2Game"], vm.VirtualJoystickGames.Select(g => g.Id).ToList());
        Assert.True(vm.VirtualJoystickGames[0].IsVirtual);
        Assert.False(vm.VirtualJoystickGames[2].IsVirtual);
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
        // The game's name (never its id) and the pretend controller's words (third novice review, minor 4).
        Assert.Contains("These do not fit: VKB Stick JOY_BTN30", vm.VirtualJoystickNeedsText);
        Assert.DoesNotContain("HLLVietnam", vm.VirtualJoystickNeedsText);
        Assert.Contains("on the pretend controller", vm.VirtualJoystickNeedsText);
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
        Assert.Contains("pretend controller, a virtual Xbox 360 controller", vm.VirtualJoystickMessage);

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        FakeFfbHostProcess host = Assert.Single(launcher.Started);
        Assert.True(Seq(host.Arguments, "--xbox-sink", "dry"));
        host.Emit("xout -32768 32767 0 0 255 0 1001");
        VirtualJoystickLiveDevice pad = vm.VirtualJoystickLive.Single(d => d.Id == 0);
        Assert.Equal("Virtual Xbox 360 pad: table of Hell Let Loose: Vietnam", pad.Title);
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
        // A game on the Xbox pad: the plain line from the first read on (final novice review F10).
        Assert.Equal("The pretend controller needs the free helper ViGEmBus. Install it in the card below.", vm.ViGEmStatusText);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Empty(launcher.Started);
        Assert.Contains("ViGEmBus driver is not installed", vm.VirtualJoystickMessage);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("apply-hidhide"));

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Single(launcher.Started);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
    }
    // ACE COMBAT 8 reads only Xbox controllers: with no mode chosen it is on the Xbox pad and the page says why; the
    // user's choice ends that (owner request 2026-11-14).
    [Fact]
    public async Task XboxOnlyGame_IsOnThePadByItselfAndSaysWhy_UserChoiceWins()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6 };
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);

        VirtualJoystickGameItem ac8 = vm.VirtualJoystickGames.Single(g => g.Id == "AceCombat8");
        Assert.True(ac8.IsXbox);
        Assert.True(ac8.ShowAutoMode);
        Assert.Contains("reads only Xbox controllers", ac8.AutoModeText);
        Assert.False(ac8.ShowSuggestion);
        Assert.True(vm.HasXboxGames);

        ac8.ModeIndex = 0;
        await WaitUntilAsync(() => engine.Calls.Any(c => Seq(c, "vjoy", "mode", "AceCombat8", "native")));
        await WaitUntilAsync(() => !vm.IsBusy);
        ac8 = vm.VirtualJoystickGames.Single(g => g.Id == "AceCombat8");
        Assert.False(ac8.IsXbox);
        Assert.False(ac8.ShowAutoMode);
        Assert.Equal(string.Empty, ac8.AutoModeText);
        Assert.True(ac8.ShowSuggestion);
        // The status line says the latest switch, not an earlier "...virtual Xbox pad: done.".
        Assert.Contains("uses your physical devices again", vm.StatusText);
    }

    [Fact]
    public async Task XboxOnlyGame_TheProfileDoesNotUse_StaysOffTheList()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync();

        Assert.DoesNotContain(vm.VirtualJoystickGames, g => g.Id == "AceCombat8");
        Assert.False(vm.HasXboxGames);
        Assert.False(vm.VirtualJoystickNeedsViGEm);
    }

    // A title-screen game (ACE COMBAT 8) that is not running yet never sees a pretend controller made now: Start says so
    // plainly (with the way back from a full-screen game); with the game running it does not. Auto start's tooltip
    // names the game as one auto start is too early for.
    [Fact]
    public async Task TitleScreenGame_StartBeforeTheGame_SaysTooEarly()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6 };
        engine.Modes["DCS"] = "native";
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);
        Assert.True(vm.VirtualJoystickPadOnly);
        vm.VirtualJoystickFindGame = _ => null;
        Assert.Equal("ACE COMBAT 8: Wings of Theve", vm.VirtualJoystickTooEarlyGame());
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.StartsWith("Too early: ACE COMBAT 8", vm.VirtualJoystickMessage);
        Assert.Contains("Alt and press Tab", vm.VirtualJoystickMessage);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        vm.VirtualJoystickFindGame = names => names.Contains("AceCombat8") ? 4242 : null;
        Assert.Equal(string.Empty, vm.VirtualJoystickTooEarlyGame());
        vm.VirtualJoystickMessage = string.Empty;
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Too early", vm.VirtualJoystickMessage);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Contains("Not for ACE COMBAT 8", vm.VirtualJoystickAutoStartTip);
        Assert.DoesNotContain("HidHide", vm.VirtualJoystickStartTip);
    }

    [Fact]
    public async Task XboxOnlyGame_WithoutViGEm_ThePageSaysSoWithTheOneClickInstall()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6, ViGEmInstalled = false };
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);

        Assert.True(vm.VirtualJoystickNeedsViGEm);
        Assert.Contains("ACE COMBAT 8", vm.VirtualJoystickViGEmMissingText);
        Assert.Contains("needs the free helper ViGEmBus", vm.VirtualJoystickViGEmMissingText);
        Assert.Contains("Tick the box below", vm.VirtualJoystickViGEmMissingText);
        Assert.DoesNotContain("Refresh", vm.VirtualJoystickViGEmMissingText);
        Assert.Equal("vigembus", vm.VirtualJoystickViGEmComponent?.Id);
    }

    // The games list (and the function game picker that follows it) by the name shown, case ignored, in the user's
    // language rules (owner request 2026-11-14).
    [Fact]
    public async Task Games_AreSortedByTheirShownName()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6 };
        engine.Modes["IL2Game"] = "virtual";
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(engine);

        List<string> names = vm.VirtualJoystickGames.Select(g => g.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Create(JoyPro.Gui.Localization.Loc.Culture, ignoreCase: true)).ToList(), names);
        Assert.Equal("ACE COMBAT 8: Wings of Theve", names[0]);
        List<string> picker = vm.VirtualFunctionGames.Select(g => g.Name).ToList();
        Assert.Equal(picker.OrderBy(n => n, StringComparer.Create(JoyPro.Gui.Localization.Loc.Culture, ignoreCase: true)).ToList(), picker);
    }

    // Several games on the pad (the owner's ACE COMBAT 7 + 8): each keeps its table; the running game's goes live, else
    // the one picked on the page (saved), never one dropped silently.
    [Fact]
    public async Task SeveralXboxGames_TheRunningGameElseThePickGoesLive()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6, XboxPadGame = "HLLVietnam" };
        engine.Modes["HLLVietnam"] = "xbox";
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateVmAsync(engine);

        Assert.True(vm.HasSeveralXboxGames);
        Assert.Equal(["AceCombat8", "HLLVietnam"], vm.VirtualJoystickXboxGames.Select(g => g.Id).ToList());
        Assert.Equal("HLLVietnam", vm.VirtualJoystickXboxPadGame?.Id);

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Contains(engine.Calls, c => Seq(c, "vjoy", "forwarder-config") && Seq(c, "--xbox-game", "HLLVietnam"));
        Assert.Equal("HLLVietnam", vm.VirtualJoystickLiveXboxGame);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        vm.VirtualJoystickXboxPadGame = vm.VirtualJoystickXboxGames.Single(g => g.Id == "AceCombat8");
        await WaitUntilAsync(() => engine.Calls.Any(c => Seq(c, "vjoy", "settings", "--xbox-pad-game", "AceCombat8")));
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal("AceCombat8", engine.XboxPadGame);
        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        Assert.True(Seq(engine.Calls.Last(c => Seq(c, "vjoy", "forwarder-config")), "--xbox-game", "AceCombat8"));
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        // Hell Let Loose: Vietnam runs: its table, whatever is picked.
        vm.VirtualJoystickFindGame = names => names.Contains("HLLVietnam-Win64-Shipping") ? 4242 : null;
        Assert.Equal("HLLVietnam", vm.ChooseVirtualJoystickXboxGame(null));
        Assert.Equal("HLLVietnam", vm.ChooseVirtualJoystickXboxGame(4242));
        Assert.Equal(2, vm.VirtualJoystickGames.Count(g => g.IsXbox));
    }

    // The live test names what each mapped pad control does and where it comes from (owner: "I could not tell that the
    // gun sits on A").
    [Fact]
    public async Task LiveView_PadControlsShowWhatTheyDoAndWhereFrom()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6 };
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateVmAsync(engine);

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        FakeFfbHostProcess host = Assert.Single(launcher.Started);
        host.Emit("xout 0 0 0 0 0 0 9000"); // A + Y pressed
        VirtualJoystickLiveDevice pad = vm.VirtualJoystickLive.Single(d => d.Id == 0);
        Assert.Equal("Virtual Xbox 360 pad: table of ACE COMBAT 8: Wings of Theve", pad.Title);
        Assert.Equal("Roll (Rhino X)", pad.Axes.Single(a => a.Name == "LX").Mapping);
        Assert.Equal(string.Empty, pad.Axes.Single(a => a.Name == "RY").Mapping);
        VirtualJoystickPadControlItem gun = pad.Controls.Single(c => c.Name == "A");
        Assert.Equal("A: Fire machine gun (VKB Gunfighter BTN1)", gun.Text);
        Assert.True(gun.Pressed);
        Assert.True(pad.Controls.Single(c => c.Name == "Y").Pressed);
        Assert.Equal("Y: Switch target (VKB Gunfighter BTN_POV2_R)", pad.Controls.Single(c => c.Name == "Y").Text);
        Assert.False(pad.Controls.Single(c => c.Name == "D-pad").Pressed);
        host.Emit("xout 0 0 0 0 0 0 0001");
        Assert.False(gun.Pressed);
        Assert.True(pad.Controls.Single(c => c.Name == "D-pad").Pressed);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
    }

    // The axis panel says plainly that JoyPro applies the curve itself on the pad (binds tuning show "XboxPadGames").
    [Fact]
    public async Task AxisPanel_SaysJoyProAppliesTheCurveOnTheXboxPad()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(refresh: false);
        AxisTuningScopeItem game = new("game:AceCombat8", "ACE COMBAT 8", "AceCombat8", string.Empty);
        AxisTuningScopeItem dcs = new("game:DCS", "DCS", "DCS", string.Empty);
        vm.AxisTuningScopes.Add(new AxisTuningScopeItem("base", "All games", "DCS", string.Empty));
        vm.AxisTuningScopes.Add(game);
        vm.AxisTuningScopes.Add(dcs);
        using JsonDocument show = JsonDocument.Parse("""{"Scopes":{"base":"inv=0"},"Uses":[],"XboxPadGames":["AceCombat8"]}""");
        vm.ReadAxisTuningShow(show.RootElement);

        vm.SelectedAxisTuningScope = game;
        Assert.True(vm.HasAxisTuningXboxPadNote);
        Assert.Contains("JoyPro applies this curve itself on the virtual Xbox pad for ACE COMBAT 8", vm.AxisTuningXboxPadNote);
        vm.SelectedAxisTuningScope = dcs;
        Assert.False(vm.HasAxisTuningXboxPadNote);
        vm.SelectedAxisTuningScope = vm.AxisTuningScopes[0];
        Assert.Contains("ACE COMBAT 8", vm.AxisTuningXboxPadNote);
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

    // The live view switch sends JoyPro.VJoy's protocol words, never their translation (in German the forwarder got
    // "Überwachung an" and the live view stayed empty; found in the vJoy live test 2026-11-01).
    [Fact]
    public async Task LiveView_SendsTheProtocolCommandUntranslated()
    {
        try
        {
            JoyPro.Gui.Localization.Loc.SetLanguage("de");
            (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateVmAsync();
            await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
            FakeFfbHostProcess host = Assert.Single(launcher.Started);

            vm.VirtualJoystickLiveView = !vm.VirtualJoystickLiveView;
            vm.VirtualJoystickLiveView = !vm.VirtualJoystickLiveView;

            Assert.Contains("monitor on", host.Sent);
            Assert.Contains("monitor off", host.Sent);
            Assert.All(host.Sent, line => Assert.Matches("^(monitor on|monitor off|stop)$", line));
            await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
        }
        finally
        {
            JoyPro.Gui.Localization.Loc.SetLanguage("en");
        }
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

    // Test changes nothing in the profile: the mapping is only checked (vjoy map --dry-run, no history version); Start
    // saves it.
    [Fact]
    public async Task Test_ChecksTheMappingWithoutSavingIt_StartSavesIt()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, _) = await CreateVmAsync();

        await vm.TestVirtualJoystickCommand.ExecuteAsync(null);
        IReadOnlyList<string> check = engine.Calls.Single(c => Seq(c, "vjoy", "map"));
        Assert.Contains("--dry-run", check);
        Assert.DoesNotContain("--history-note", check);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);

        engine.Calls.Clear();
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        IReadOnlyList<string> save = engine.Calls.Single(c => Seq(c, "vjoy", "map"));
        Assert.DoesNotContain("--dry-run", save);
        Assert.Contains("--history-note", save);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
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

    // ------------------------------------------------------------------ HidHideCLI 1.5.230 as it really behaves
    // (owner report 2026-11-14: "engine.exe comes up with a UAC prompt every 2 minutes"). HidHideCLI checks every
    // command before it runs it and stops at the first it refuses: the reason goes to stderr, NOTHING of the call is
    // applied and the exit code is still 0 (HidHideCLI/src/CommandInterpreter.cpp Start returns before
    // ApplyConfigurationChanges). Its volume check is case sensitive, so Steam's "c:\program files (x86)\..." made it
    // drop the whole session (cloaking, hidden devices, program list) and every Start ended in "did not take every change".

    // The owner's HidHide when it happened: cloaking off, inverse on, his own (dormant) list incl. the vJoy device, and
    // five program entries HidHide itself cannot name.
    private static readonly string[] OwnerDevices =
    [
        VJoy, UserDevice, @"HID\VID_31E3&PID_1231&MI_00\b&1d690f3a&0&0000", @"HID\VID_3344&PID_80CB&Col01\c&2eaeded2&0&0000",
        @"USB\VID_231D&PID_0125\B&314E59B0&0&2", @"USB\VID_4098&PID_BE04\153250A2D416022153461032", Stick
    ];

    private const string Ac8 = @"H:\SteamLibrary\steamapps\common\ACE COMBAT 8\Game\Binaries\Win64\AceCombat8.exe";
    private const string Ac8Launcher = @"H:\SteamLibrary\steamapps\common\ACE COMBAT 8\start_protected_game.exe";
    private const string HllvSteamSpelling = @"c:\program files (x86)\steam\steamapps\common\Hell Let Loose - Vietnam\HLLVietnam\Binaries\Win64\HLLVietnam-Win64-Shipping.exe";
    private const string HllvHidHideSpelling = @"C:\program files (x86)\steam\steamapps\common\Hell Let Loose - Vietnam\HLLVietnam\Binaries\Win64\HLLVietnam-Win64-Shipping.exe";

    // The exact output of HidHideCLI 1.5.230, one query per call (std::wcout, CRLF): Commands.cpp CloakState,
    // InvState, AppList ("--app-reg \"" + path + "\"", an empty path for an entry it cannot name) and DevList.
    private const string RealCloakState = "--cloak-off\r\n";
    private const string RealInvState = "--inv-on\r\n";
    private const string RealAppList = "--app-reg \"\"\r\n--app-reg \"\"\r\n--app-reg \"H:\\SteamLibrary\\steamapps\\common\\ACE COMBAT 8\\Game\\Binaries\\Win64\\AceCombat8.exe\"\r\n" +
                                       "--app-reg \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Some Game\\Game.exe\"\r\n--app-reg \"\"\r\n";
    private const string RealDevList = "--dev-hide \"HID\\HIDCLASS\\1&2d595ca7&1&0000\"\r\n--dev-hide \"HID\\VID_231D&PID_0125\\c&8abe679&0&0000\"\r\n--dev-hide \"USB\\VID_4098&PID_BE04\\153250A2D416022153461032\"\r\n";

    [Fact]
    public void RealCliOutput_InverseModeAppListWithSpacesDriveHAndX86_IsReadExactly()
    {
        HidHideState state = new();
        Assert.True(HidHideState.TryParsePart(HidHideState.PartCloak, RealCloakState, state, out _));
        Assert.True(HidHideState.TryParsePart(HidHideState.PartInverse, RealInvState, state, out _));
        Assert.True(HidHideState.TryParsePart(HidHideState.PartApps, RealAppList, state, out _));
        Assert.True(HidHideState.TryParsePart(HidHideState.PartDevices, RealDevList, state, out _));

        Assert.True(state.Complete);
        Assert.False(state.CloakOn);
        Assert.True(state.InverseOn); // inverse on = the listed programs are the ones that lose the hidden devices
        Assert.Equal([Ac8, @"C:\Program Files (x86)\Steam\steamapps\common\Some Game\Game.exe"], state.Apps);
        Assert.Equal(3, state.UnnamedApps); // "--app-reg \"\"": HidHide's own entries it cannot name, not a parse error
        Assert.Equal([VJoy, Stick, @"USB\VID_4098&PID_BE04\153250A2D416022153461032"], state.Devices);
        // Read back after writing in the same format: nothing lost.
        HidHideState again = HidHideState.Parse(state.Describe());
        Assert.Equal(state.Apps, again.Apps);
        Assert.Equal(state.UnnamedApps, again.UnnamedApps);
        Assert.Equal(state.Devices, again.Devices);

        // An empty program list prints nothing at all: an answer, not an error. Access denied is never "empty".
        HidHideState empty = new();
        Assert.True(HidHideState.TryParsePart(HidHideState.PartApps, string.Empty, empty, out _));
        Assert.False(HidHideState.TryParsePart(HidHideState.PartDevices, "Error code 0x00000005 Access is denied.\r\n", empty, out _));
        Assert.True(empty.DevicesUnknown);
    }

    [Fact]
    public void RealCliRules_RefuseWhatHidHideCliRefuses()
    {
        Assert.Equal(string.Empty, HidHideCliRules.AppPathProblem(Ac8));
        Assert.Equal(string.Empty, HidHideCliRules.AppPathProblem(HllvHidHideSpelling));
        Assert.Contains("volume is case sensitive", HidHideCliRules.AppPathProblem(HllvSteamSpelling));
        Assert.Contains("executable", HidHideCliRules.AppPathProblem(@"C:\Games\Old\GAME.EXE"));
        Assert.Contains("full path", HidHideCliRules.AppPathProblem(@"Game\game.exe"));
        Assert.Equal(HllvHidHideSpelling, HidHideCliRules.NormalizeAppPath(HllvSteamSpelling));
        Assert.Equal(@"C:\Games\x.exe", HidHideCliRules.NormalizeAppPath("c:/Games/x.exe"));
        Assert.Contains("too many characters", HidHideCliRules.DevicePathProblem(new string('A', 201)));
        // Exit code 0 with a reason on stderr is HidHideCLI refusing the call.
        Assert.Equal(HidHideCli.Rejected, HidHideCli.ApplyOutcome(0, "The file name should be located on a storage volume (volume is case sensitive).\r\n"));
        Assert.Equal(0, HidHideCli.ApplyOutcome(0, "  "));
        Assert.Equal(5, HidHideCli.ApplyOutcome(5, "x"));

        // The fake CLI refuses like the real one: nothing applied, the reason kept.
        FakeHidHideCli cli = new(Path.Combine(folder, "refuse.txt"));
        HidHideState before = State(cloak: false, inverse: true, devices: [Stick], apps: []);
        cli.Write(before);
        Assert.Equal(HidHideCli.Rejected, cli.Apply(["--dev-unhide", Stick, "--app-reg", HllvSteamSpelling, "--cloak-on"]));
        Assert.Contains("--app-reg", cli.LastError);
        AssertSame(before, cli.ReadState());
    }

    [Fact]
    public void Enable_OwnersSetupWithSteamLowerCaseDrive_HidesWithOneCallAndRestoresExactly()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: OwnerDevices.ToList(), apps: []);
        before.UnnamedApps = 5;
        (FakeHidHideCli cli, HidHideSession session) = Session(before);
        HidHideRequest request = new()
        {
            HideDevices = [Stick, Throttle],
            KeepVisible = [VJoy],
            GameExes = [Ac8, Ac8Launcher, HllvSteamSpelling],
            TrustedExes = [Forwarder, @"D:\JoyPro2-test\JoyPro.Gui.exe"],
            Games = ["AceCombat8", "HLLVietnam"],
            OwnerPid = 777,
            OwnerStartTicks = 1
        };

        HidHideResult enabled = session.Enable(request, dryRun: false);

        Assert.True(enabled.Ok, enabled.Message + " " + string.Join(" ", enabled.Problems));
        Assert.Single(cli.Calls); // one administrator prompt
        Assert.DoesNotContain(cli.Calls[0], a => a.StartsWith("c:", StringComparison.Ordinal));
        HidHideState during = cli.ReadState();
        Assert.True(during.CloakOn);
        Assert.True(during.InverseOn);
        Assert.Equal(new[] { Ac8, Ac8Launcher, HllvHidHideSpelling }.OrderBy(a => a), during.Apps.OrderBy(a => a));
        Assert.Equal(new[] { Stick, Throttle }.OrderBy(d => d), during.Devices.OrderBy(d => d));
        Assert.False(during.HasDevice(VJoy));
        Assert.Equal(5, during.UnnamedApps);
        Assert.False(enabled.Blocking);

        HidHideResult disabled = session.Disable(dryRun: false);

        Assert.True(disabled.Ok, disabled.Message + " " + string.Join(" ", disabled.Problems));
        Assert.Equal(2, cli.Calls.Count);
        HidHideState after = cli.ReadState();
        AssertSame(before, after);
        Assert.Equal(5, after.UnnamedApps);
        Assert.Null(session.LoadJournal());
    }

    [Fact]
    public void Enable_HidHideCliRefusesTheCall_SaysSoAndLeavesNoSession()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: OwnerDevices.ToList(), apps: []);
        FakeHidHideCli inner = new(Path.Combine(folder, "hidhide-state.txt"));
        inner.Write(before);
        RefusingHidHideCli cli = new(inner);
        HidHideSession session = new(cli, Path.Combine(folder, "hidhide-session.txt"), (_, _) => true);

        HidHideResult result = session.Enable(Request(owner: 777), dryRun: false);

        Assert.False(result.Ok);
        Assert.False(result.Changed);
        Assert.False(result.Blocking);
        Assert.Equal("none", result.SessionPhase);
        Assert.Contains("did not change anything", result.Message);
        Assert.Contains("volume is case sensitive", Assert.Single(result.Problems));
        Assert.Null(session.LoadJournal()); // nothing waits for a restore, no recover prompt at the next start
        AssertSame(before, inner.ReadState());
    }

    [Fact]
    public void Enable_VJoyDeviceStaysHidden_IsBlocking()
    {
        HidHideState before = State(cloak: false, inverse: true, devices: [VJoy, UserDevice], apps: []);
        FakeHidHideCli inner = new(Path.Combine(folder, "hidhide-state.txt"));
        inner.Write(before);
        HidHideSession session = new(new StickyDeviceHidHideCli(inner, VJoy), Path.Combine(folder, "hidhide-session.txt"), (_, _) => true);

        HidHideResult result = session.Enable(Request(owner: 777), dryRun: false);

        Assert.False(result.Ok);
        Assert.True(result.Blocking);
        Assert.Contains(result.Problems, p => p.Contains(VJoy));
    }

    [Fact]
    public void WithoutAdministratorRights_NothingToChange_NeedsNoPrompt()
    {
        // The owner's state after the failed starts: a stale session whose changes never took.
        HidHideState before = State(cloak: false, inverse: true, devices: OwnerDevices.ToList(), apps: []);
        before.UnnamedApps = 5;
        (FakeHidHideCli cli, HidHideSession session) = Session(before, (_, _) => false);
        File.WriteAllText(session.JournalPath, new HidHideJournal
        {
            Phase = HidHideJournal.PhaseActive,
            OwnerPid = 58884,
            Previous = before.Clone(),
            SetCloak = true,
            Games = ["AceCombat8", "HLLVietnam"],
            AddedApps = [Ac8, Ac8Launcher, HllvSteamSpelling],
            RemovedDevices = OwnerDevices.Where(d => d != Stick).ToList()
        }.Serialize());

        HidHideResult restored = session.DisableIfNothingToChange()!;

        Assert.NotNull(restored);
        Assert.True(restored.Ok, restored.Message);
        Assert.Empty(cli.Calls); // no HidHideCLI change, so no administrator prompt
        Assert.Null(session.LoadJournal());
        AssertSame(before, cli.ReadState());

        // Enable: nothing to change -> no prompt; something to change -> null (the elevated step does it).
        HidHideState ready = State(cloak: true, inverse: true, devices: [Stick, Throttle], apps: [Dcs]);
        cli.Write(ready);
        Assert.True(session.EnableIfNothingToChange(Request(owner: 777))!.Ok);
        Assert.Empty(cli.Calls);
        cli.Write(before);
        Assert.Null(session.EnableIfNothingToChange(Request(owner: 777)));
        Assert.Empty(cli.Calls);
        // HidHide not completely readable without the rights: never a guess, the elevated step decides.
        cli.Denied.Add(HidHideState.PartDevices);
        Assert.Null(session.EnableIfNothingToChange(Request(owner: 777)));
    }

    [Fact]
    public async Task AutoStart_FailedAdministratorStep_IsNotRepeatedWhileTheGameRuns()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        engine.Cli.ApplyExitCode = HidHideCli.Cancelled;
        vm.VirtualJoystickAutoStart = true;
        int? running = 5150;
        vm.VirtualJoystickFindGame = names => names.Contains("DCS") ? running : null;

        for (int i = 0; i < 5; i++)
            await vm.CheckVirtualJoystickGamesOnceAsync();

        Assert.Single(engine.Calls, c => c.Contains("apply-hidhide")); // one prompt, not one every few seconds
        Assert.Empty(launcher.Started);
        Assert.True(vm.VirtualJoystickCanRetry);
        Assert.Contains("Try again", vm.VirtualJoystickMessage);

        // The game ends and starts again: that start gets its one try.
        running = null;
        await vm.CheckVirtualJoystickGamesOnceAsync();
        running = 6000;
        await vm.CheckVirtualJoystickGamesOnceAsync();
        await vm.CheckVirtualJoystickGamesOnceAsync();
        Assert.Equal(2, engine.Calls.Count(c => c.Contains("apply-hidhide")));

        // Try again by hand: once more, and now it works.
        engine.Cli.ApplyExitCode = 0;
        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);
        Assert.Equal(3, engine.Calls.Count(c => c.Contains("apply-hidhide")));
        Assert.Single(launcher.Started);
        Assert.False(vm.VirtualJoystickCanRetry);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
        vm.VirtualJoystickAutoStart = false;
    }

    [Fact]
    public async Task Start_HidHideRefusesTheCall_ForwardsAnywayWithAWarning()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();
        engine.Session = new HidHideSession(new RefusingHidHideCli(engine.Cli), Path.Combine(folder, "data", "hidhide-session.txt"), (_, _) => true);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Single(launcher.Started);
        Assert.True(vm.VirtualJoystickRunning);
        Assert.Contains("did not hide everything", vm.VirtualJoystickMessage);
        Assert.Contains("volume is case sensitive", vm.VirtualJoystickMessage);
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
        // Nothing was changed, so nothing is restored (no second prompt).
        Assert.DoesNotContain(engine.Calls, c => c.Contains("restore-hidhide"));
        AssertSame(before, engine.Cli.ReadState());
    }

    [Fact]
    public async Task Start_VJoyDeviceStaysHidden_StartsNothingAndRestores()
    {
        (MainWindowViewModel vm, VJoyFakeEngine engine, FakeFfbLauncher launcher) = await CreateVmAsync();
        HidHideState before = engine.Cli.ReadState();
        engine.Session = new HidHideSession(new StickyDeviceHidHideCli(engine.Cli, VJoy), Path.Combine(folder, "data", "hidhide-session.txt"), (_, _) => true);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Empty(launcher.Started);
        Assert.False(vm.VirtualJoystickRunning);
        Assert.Contains("hides the virtual joystick", vm.VirtualJoystickMessage);
        Assert.Single(engine.Calls, c => c.Contains("restore-hidhide"));
        AssertSame(before, engine.Cli.ReadState());
        Assert.True(vm.VirtualJoystickCanRetry);
    }

    [Fact]
    public async Task Start_OnlyXboxOnlyGames_NeedsNoHidHideAndNoPrompt()
    {
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6 };
        engine.Modes["DCS"] = "native";
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await CreateVmAsync(engine);

        await vm.StartVirtualJoystickCommand.ExecuteAsync(null);

        Assert.Single(launcher.Started);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("apply-hidhide"));
        await vm.StopVirtualJoystickCommand.ExecuteAsync(null);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("restore-hidhide"));
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

// HidHideCLI refusing a call (as 1.5.230 does for "c:\..."): nothing applied, the reason on stderr, exit code 0.
internal sealed class RefusingHidHideCli(FakeHidHideCli inner) : IHidHideCli
{
    public List<IList<string>> Calls { get; } = [];

    public bool Installed => inner.Installed;

    public string Location => inner.Location;

    public string LastError { get; private set; } = string.Empty;

    public HidHideRead Read() => inner.Read();

    public int Apply(IList<string> arguments)
    {
        Calls.Add(arguments.ToList());
        LastError = "--app-reg \"c:\\games\\x.exe\": The file name should be located on a storage volume (volume is case sensitive).";
        return HidHideCli.ApplyOutcome(0, LastError);
    }
}

// HidHide that takes every change but cannot show one device again (it stays hidden).
internal sealed class StickyDeviceHidHideCli(FakeHidHideCli inner, string sticky) : IHidHideCli
{
    public bool Installed => inner.Installed;

    public string Location => inner.Location;

    public string LastError => inner.LastError;

    public HidHideRead Read() => inner.Read();

    public int Apply(IList<string> arguments)
    {
        List<string> kept = [];
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "--dev-unhide" && i + 1 < arguments.Count && string.Equals(arguments[i + 1], sticky, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            kept.Add(arguments[i]);
        }
        return inner.Apply(kept);
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

    public Dictionary<string, string> Modes { get; } = new(StringComparer.OrdinalIgnoreCase) { ["DCS"] = "virtual", ["IL2Game"] = "native", ["HLLVietnam"] = "native", ["AceCombat8"] = "xbox" };

    // Games on the Xbox pad by themselves (no mode chosen, they read only Xbox controllers); a mode command ends it.
    public HashSet<string> AutoModes { get; } = new(StringComparer.OrdinalIgnoreCase) { "AceCombat8" };

    // ACE COMBAT 8's relations: 0 = the profile does not use it (the page leaves it out).
    public int AceCombat8Relations { get; set; }

    // engine vjoy settings --xbox-pad-game.
    public string XboxPadGame { get; set; } = string.Empty;

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
                AutoModes.Remove(args[at + 2]);
                return (0, Json(new { Message = "ok" }));
            case "settings":
                int pick = args.IndexOf("--xbox-pad-game");
                if (pick >= 0)
                    XboxPadGame = args[pick + 1];
                return (0, Json(new { Message = "Saved." }));
            case "map":
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
        (result.Cancelled ? 2 : result.Ok ? 0 : 1, Json(new { result.Ok, result.Changed, result.Cancelled, result.Blocking, result.Message, result.Strategy, result.Commands, result.Problems, result.SessionPhase }));

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
            Settings = new { FirstDevice = 1, LastDevice = 16, AutoStart = false, HideDevices = true, XboxPadGame },
            Games = new object[]
            {
                new { Id = "DCS", Name = "DCS World", Mode = Modes["DCS"], Relations = 12, ProcessNames = new[] { "DCS" }, Exes = new[] { @"D:\DCS World\bin\DCS.exe" }, GamepadLayout = 0, WritesGamepadBinds = false },
                new { Id = "IL2Game", Name = "IL-2 Sturmovik: Great Battles", Mode = Modes["IL2Game"], Relations = 3, ProcessNames = new[] { "Il-2" }, Exes = Array.Empty<string>(), GamepadLayout = 0, WritesGamepadBinds = false },
                new { Id = "HLLVietnam", Name = "Hell Let Loose: Vietnam", Mode = Modes["HLLVietnam"], Relations = 4, ProcessNames = new[] { "HLLVietnam-Win64-Shipping" }, Exes = new[] { @"H:\HLLV\HLLVietnam-Win64-Shipping.exe" }, GamepadLayout = 9, WritesGamepadBinds = false, SuggestedMode = "xbox", MaxJoysticks = 0, DeviceLimitNote = "Reads no joystick, only Xbox controllers (XInput): fly it with the virtual Xbox pad." },
                new { Id = "MSFS", Name = "Microsoft Flight Simulator", Mode = "native", Relations = 0, ProcessNames = new[] { "FlightSimulator" }, Exes = Array.Empty<string>() },
                new { Id = "AceCombat8", Name = "ACE COMBAT 8: Wings of Theve", Mode = Modes["AceCombat8"], AutoMode = AutoModes.Contains("AceCombat8"), ReadsOnlyXbox = true, Relations = AceCombat8Relations, ProcessNames = new[] { "AceCombat8" }, Exes = Array.Empty<string>(), GamepadLayout = 13, WritesGamepadBinds = false, SuggestedMode = "xbox", MaxJoysticks = 0, DeviceLimitNote = "Reads Xbox controllers (XInput) and a few licensed sticks, no generic joystick: fly it with the virtual Xbox pad." }
            },
            Mapping = new object[]
            {
                new { Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_X", Device = 1, Target = "JOY_X", Connected = true, Used = true, New = false, Relations = new[] { "Roll" } },
                new { Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_BTN1", Device = 1, Target = "JOY_BTN1", Connected = true, Used = true, New = false, Relations = new[] { "Fire" } }
            },
            ViGEm = new { Installed = ViGEmInstalled, Running = ViGEmInstalled, Version = ViGEmInstalled ? "1.22.0" : "", Fake = false, InstallerUrl = "https://github.com/nefarius/ViGEmBus/releases" },
            XboxMapping = (Modes["HLLVietnam"] != "xbox" ? Array.Empty<object>() : new object[]
            {
                new { Game = "HLLVietnam", Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_X", Target = "XB_LX", TargetName = "Left stick X", Pinned = true, Action = "Driver_RollRight", Connected = true, Used = true, New = false, Relations = new[] { "Roll" } },
                new { Game = "HLLVietnam", Source = "VKB Stick {11111111-2222-3333-4444-000000000001}", SourceName = "VKB Stick", Input = "JOY_BTN1", Target = "XB_A", TargetName = "A", Pinned = false, Action = "", Connected = true, Used = true, New = false, Relations = new[] { "Flare" } }
            }).Concat(Modes["AceCombat8"] != "xbox" || AceCombat8Relations == 0 ? Array.Empty<object>() : new object[]
            {
                new { Game = "AceCombat8", Source = "VKB Gunfighter {11111111-2222-3333-4444-000000000003}", SourceName = "VKB Gunfighter", Input = "JOY_BTN1", Target = "XB_A", TargetName = "A", Pinned = true, Action = "Pad_FireMachineGun", Connected = true, Used = true, New = false, Relations = new[] { "Fire machine gun" } },
                new { Game = "AceCombat8", Source = "Rhino {11111111-2222-3333-4444-000000000004}", SourceName = "Rhino", Input = "JOY_X", Target = "XB_LX", TargetName = "Left stick X", Pinned = true, Action = "Pad_Roll", Connected = true, Used = true, New = false, Relations = new[] { "Roll" } },
                new { Game = "AceCombat8", Source = "VKB Gunfighter {11111111-2222-3333-4444-000000000003}", SourceName = "VKB Gunfighter", Input = "JOY_BTN_POV2_R", Target = "XB_Y", TargetName = "Y", Pinned = true, Action = "Pad_SwitchTarget", Connected = true, Used = true, New = false, Relations = new[] { "Switch target" } },
                new { Game = "AceCombat8", Source = "VKB Gunfighter {11111111-2222-3333-4444-000000000003}", SourceName = "VKB Gunfighter", Input = "POV1", Target = "XB_DPAD", TargetName = "D-pad", Pinned = false, Action = "", Connected = true, Used = true, New = false, Relations = new[] { "Look around" } }
            }).ToArray(),
            XboxGames = (Modes["HLLVietnam"] != "xbox" ? Array.Empty<object>() : new object[] { new { Game = "HLLVietnam", Slots = 2, Unfit = new[] { "VKB Stick JOY_BTN30 (Gear) does not fit: every Xbox button is taken." }, Notes = Array.Empty<string>() } })
                .Concat(Modes["AceCombat8"] != "xbox" || AceCombat8Relations == 0 ? Array.Empty<object>() : new object[] { new { Game = "AceCombat8", Slots = 4, Unfit = Array.Empty<string>(), Notes = Array.Empty<string>() } }).ToArray(),
            Needs = new[] { new { Id = 1, Slots = 2, Sufficient = true, Missing = Array.Empty<string>(), Current = "x,y,z,rx,ry,rz,sl0,sl1, 32 buttons, 4 hats", Recommended = "same", ConfigCommand = "" } },
            RefreshRateHz = 144,
            Errors = Array.Empty<string>(),
            Warnings = Array.Empty<string>()
        };
        return Json(status);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
