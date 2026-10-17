using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Replace a device (docs/devices.md) with a fake engine: the notice after a profile opened and devices came back with a
// new ID (engine devices auto-remap), and the Replace device window (engine devices replace: preview, changed inputs,
// merge choice, apply).
[SupportedOSPlatform("windows")]
public sealed class DeviceReplaceTests
{
    private const string OldStick = "CLSE Joystick VPC V.F.X F14 DirectX {B618E440-C7EB-11ed-8003-444553540000}";
    private const string NewStick = "VPforce Rhino FFB Joystick {AD5168C0-B52F-11f1-8001-444553540000}";

    [Fact]
    public async Task AutoRemap_ShowsWhatMoved_AndWhatWasLeft()
    {
        ReplaceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();

        await vm.RunDeviceAutoRemapAsync();

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "devices", "auto-remap"));
        Assert.Contains("--history-note", call);
        Assert.DoesNotContain("--dry-run", call);
        Assert.True(vm.HasDeviceRemapNotice);
        Assert.True(vm.CanUndoDeviceRemap);
        DeviceRemapNoticeItem item = Assert.Single(vm.DeviceRemapNoticeItems);
        Assert.StartsWith("LEFT VPC Throttle MT-50 CM: 127 bind(s), 1 modifier(s) moved", item.Text);
        Assert.Contains("{42CEC570-0A3F-11f0-800B-444553540000}", item.Text);
        Assert.Contains("vJoy Device", vm.DeviceRemapSkippedText);
        Assert.True(vm.HasDeviceRemapSkipped);

        vm.DismissDeviceRemapNoticeCommand.Execute(null);
        Assert.False(vm.HasDeviceRemapNotice);
    }

    [Fact]
    public async Task AutoRemap_NothingToMove_NoNotice_AndOffWhenTheUserTurnedRemapOff()
    {
        ReplaceFakeEngine fake = new() { RemapApplied = false };
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();
        await vm.RunDeviceAutoRemapAsync();
        Assert.False(vm.HasDeviceRemapNotice);

        fake.Calls.Clear();
        vm.RemapMissingDevices = false;
        await vm.RunDeviceAutoRemapAsync();
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ReplaceWindow_PreviewsByLayout_AndAppliesTheUsersChoices()
    {
        ReplaceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();

        await vm.PrepareReplaceDeviceAsync(OldStick, NewStick);

        IReadOnlyList<string> preview = fake.Calls.Last(c => NoviceGuiTests.Seq(c, "devices", "replace"));
        Assert.True(NoviceGuiTests.Seq(preview, "devices", "replace", OldStick, NewStick, "--mode", "auto"));
        Assert.Contains("--dry-run", preview);
        Assert.True(vm.ReplaceHasPreview);
        Assert.Equal(3, vm.ReplaceRows.Count);
        Assert.StartsWith("Mapped by layout", vm.ReplaceSummary);
        Assert.Contains("generic layout (stick), not connected", vm.ReplaceModels);
        Assert.True(vm.ReplaceNeedsMerge);
        Assert.Contains("already has 2 binds. Where both use the same input:", vm.ReplaceMergeText);
        Assert.Equal("2 of 3 inputs fit, 4 bind(s) move. 1 stay on the old device (no matching input). The new device already uses 1 of these inputs.", vm.ReplaceCounts);

        Assert.Equal("Stay on the old device: JOY_BTN40 (Odd). Pick an input for them below if the new device has one.", vm.ReplaceUnmappedText);
        Assert.True(vm.HasReplaceUnmapped);
        DeviceReplaceRow roll = vm.ReplaceRows.Single(r => r.OldInput == "JOY_X");
        Assert.Equal("same part", roll.MethodText);
        Assert.DoesNotContain("JOY_BTN1", roll.Choices);
        DeviceReplaceRow trigger = vm.ReplaceRows.Single(r => r.OldInput == "JOY_BTN1");
        Assert.True(trigger.HasOverlap);
        Assert.Contains("Fire", trigger.OverlapText);
        DeviceReplaceRow odd = vm.ReplaceRows.Single(r => r.OldInput == "JOY_BTN40");
        Assert.False(odd.IsMapped);
        Assert.Equal(DeviceReplaceRow.LeaveOut, odd.Choices[0]);

        // The user moves the trigger elsewhere and keeps the new device's binds where both meet.
        trigger.ChosenInput = "JOY_BTN5";
        Assert.Equal("chosen by you", trigger.MethodText);
        Assert.False(trigger.HasOverlap);
        vm.ReplaceMergeKeep = true;
        fake.Calls.Clear();
        Assert.True(await vm.ApplyReplaceDeviceAsync());

        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "devices", "replace"));
        Assert.DoesNotContain("--dry-run", apply);
        Assert.True(NoviceGuiTests.Seq(apply, "--map", "JOY_BTN1=JOY_BTN5"));
        Assert.True(NoviceGuiTests.Seq(apply, "--merge", "keep"));
        Assert.True(NoviceGuiTests.Seq(apply, "--history-note", "Replaced CLSE Joystick VPC V.F.X F14 DirectX with VPforce Rhino FFB Joystick"));
        Assert.True(vm.HasDeviceRemapNotice);
        Assert.Equal("4 bind(s) of CLSE Joystick VPC V.F.X F14 DirectX moved to VPforce Rhino FFB Joystick. 1 stay on CLSE Joystick VPC V.F.X F14 DirectX (no matching input).", vm.DeviceRemapNoticeTitle);
    }

    [Fact]
    public async Task ReplaceWindow_FailedApply_KeepsTheWindowAndSaysWhy()
    {
        ReplaceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();
        await vm.PrepareReplaceDeviceAsync(OldStick, NewStick);
        fake.FailApply = true;

        Assert.False(await vm.ApplyReplaceDeviceAsync());
        Assert.StartsWith("Not replaced: VPforce Rhino FFB Joystick", vm.ReplaceStatus);
        Assert.Contains("already has 2 bind(s)", vm.ReplaceStatus);
        Assert.False(vm.HasDeviceRemapNotice);
    }

    [Fact]
    public void ReplaceRow_LeavingAnInputOut_IsTheUsersChoice()
    {
        DeviceReplaceRow row = new("JOY_BTN3", "Pinky button", "JOY_BTN7", "Pinky", "same part", string.Empty, false, ["Wheel brake"], [], [], [DeviceReplaceRow.LeaveOut, "JOY_BTN7"]);
        Assert.True(row.IsMapped);
        Assert.False(row.ChangedByUser);
        row.ChosenInput = DeviceReplaceRow.LeaveOut;
        Assert.False(row.IsMapped);
        Assert.True(row.ChangedByUser);
        Assert.Equal("chosen by you", row.MethodText);
        Assert.Equal("Wheel brake", row.UsedByText);
    }

    private static MainWindowViewModel CreateVm(ReplaceFakeEngine fake) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    private static string TempProfile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "replace.pr0file");
        File.WriteAllText(path, "{}");
        return path;
    }

    internal sealed class ReplaceFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool RemapApplied { get; set; } = true;

        public bool FailApply { get; set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            bool failed = FailApply && NoviceGuiTests.Seq(arguments, "devices", "replace") && !arguments.Contains("--dry-run");
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = failed ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = failed ? string.Empty : Output(arguments),
                StandardError = failed ? NewStick + " already has 2 bind(s). Choose how to merge: --merge add (keep both), keep (its binds win) or replace." : string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private string Output(IReadOnlyList<string> args)
        {
            if (NoviceGuiTests.Seq(args, "devices", "auto-remap"))
            {
                return "{\"DryRun\":false,\"Applied\":" + (RemapApplied ? "true" : "false") + ",\"Remapped\":" + (RemapApplied
                           ? "[{\"OldDevice\":\"LEFT VPC Throttle MT-50 CM {B0528DC0-35D1-11ec-8010-444553540000}\",\"NewDevice\":\"LEFT VPC Throttle MT-50 CM {42CEC570-0A3F-11f0-800B-444553540000}\",\"Reason\":\"same name\",\"Binds\":127,\"Modifiers\":1}]"
                           : "[]") +
                       ",\"Skipped\":[{\"OldDevice\":\"vJoy Device {65A42290-1FE9-11ed-8002-444553540000}\",\"Reason\":\"2 old devices\",\"Candidates\":[]},{\"OldDevice\":\"vJoy Device {B05302F0-35D1-11ec-8016-444553540000}\",\"Reason\":\"2 old devices\",\"Candidates\":[]}]," +
                       "\"Missing\":[],\"Warnings\":[]}\nAuto-saved profile: x";
            }
            if (NoviceGuiTests.Seq(args, "devices", "replace"))
            {
                bool dry = args.Contains("--dry-run");
                return "{\"OldDevice\":\"" + OldStick + "\",\"NewDevice\":\"" + NewStick + "\",\"RequestedMode\":\"auto\",\"Mode\":\"layout\"," +
                       "\"ModeReason\":\"a different device: inputs are mapped by their part on the device\",\"OldModel\":\"generic layout (stick)\",\"NewModel\":\"VPforce Rhino\"," +
                       "\"OldConnected\":false,\"NewConnected\":true,\"OldBinds\":5,\"OldModifiers\":0,\"TargetBinds\":2,\"NeedsMergeChoice\":true,\"Merge\":\"\",\"DryRun\":" + (dry ? "true" : "false") +
                       ",\"Applied\":" + (dry ? "false" : "true") + ",\"MovedBinds\":" + (dry ? 0 : 4) + ",\"KeptOnOld\":" + (dry ? 0 : 1) + ",\"RemovedTargetBinds\":0,\"MappedInputs\":2,\"UnmappedInputs\":1," +
                       "\"Mappings\":[" +
                       "{\"OldInput\":\"JOY_X\",\"NewInput\":\"JOY_X\",\"OldPart\":\"Stick (roll / pitch)\",\"NewPart\":\"Stick (roll / pitch)\",\"Method\":\"same part\",\"Axis\":true,\"Relations\":[\"Roll\",\"HCU_X\",\"SPACE_STRAFE_HOR\"],\"Modifiers\":[],\"TargetRelations\":[],\"Note\":\"\",\"Mapped\":true}," +
                       "{\"OldInput\":\"JOY_BTN1\",\"NewInput\":\"JOY_BTN1\",\"OldPart\":\"Trigger\",\"NewPart\":\"Trigger\",\"Method\":\"same part\",\"Axis\":false,\"Relations\":[\"Gun\"],\"Modifiers\":[],\"TargetRelations\":[\"Fire\"],\"Note\":\"\",\"Mapped\":true}," +
                       "{\"OldInput\":\"JOY_BTN40\",\"NewInput\":\"\",\"OldPart\":\"\",\"NewPart\":\"\",\"Method\":\"none\",\"Axis\":false,\"Relations\":[\"Odd\"],\"Modifiers\":[],\"TargetRelations\":[],\"Note\":\"no matching part on the new device\",\"Mapped\":false}]," +
                       "\"NewInputs\":[\"JOY_X\",\"JOY_Y\",\"JOY_BTN1\",\"JOY_BTN2\",\"JOY_BTN5\",\"JOY_BTN_POV1_U\"],\"Steps\":[],\"Warnings\":[\"VPforce Rhino FFB Joystick: the grip is not chosen\"]}";
            }
            if (NoviceGuiTests.Seq(args, "hud", "devices"))
                return "{\"Devices\":[{\"Device\":\"" + NewStick + "\",\"Name\":\"VPforce Rhino FFB Joystick\",\"Model\":\"\",\"Kind\":\"stick\",\"Buttons\":64,\"Hats\":4,\"Axes\":[{\"Input\":\"JOY_X\"}],\"Image\":\"\"}]}";
            return "{}";
        }
    }
}
