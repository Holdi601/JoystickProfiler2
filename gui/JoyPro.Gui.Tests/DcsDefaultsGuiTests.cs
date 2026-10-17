using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Settings > DCS: default binds (engine dcs strip-defaults / dcs device-luas) with a fake engine that keeps the
// switches like the engine's state file does.
[SupportedOSPlatform("windows")]
public sealed class DcsDefaultsGuiTests
{
    [Fact]
    public async Task Status_FillsTheSwitches_WithoutRunningAnything()
    {
        StripFakeEngine fake = new() { StripEnabled = true, SkipUiLayer = true };
        MainWindowViewModel vm = await CreateVmAsync(fake);

        fake.Calls.Clear();
        await vm.RefreshDcsDefaultsStatusCommand.ExecuteAsync(null);
        Assert.True(vm.DcsStripDefaults);
        Assert.True(vm.DcsStripSkipUiLayer);
        Assert.False(vm.DcsRemoveDeviceFiles);
        // Only the status call: filling the switches from the engine must not apply or restore anything.
        Assert.Single(fake.Calls);
        Assert.True(Seq(fake.Calls[0], "dcs", "strip-defaults", "status"));
        Assert.Contains("218 of 218", vm.DcsDefaultsStatus);
        Assert.Contains("UI layer kept", vm.DcsDefaultsStatus);
    }

    [Fact]
    public async Task SwitchingStripOn_AsksWithTheIntegrityWarning_AndAppliesOnYes()
    {
        StripFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(true); };
        vm.DcsSavedGamesPath = @"C:\Users\Pilot\Saved Games\DCS";
        vm.DcsInstallPath = @"D:\DCS World";

        fake.Calls.Clear();
        vm.DcsStripDefaults = true;
        await WaitForAsync(() => fake.Calls.Any(c => Seq(c, "dcs", "strip-defaults", "apply")));
        ConfirmRequest request = Assert.Single(asked);
        Assert.True(request.IsDanger);
        Assert.Contains("integrity", request.Message);
        IReadOnlyList<string> apply = fake.Calls.Single(c => Seq(c, "dcs", "strip-defaults", "apply"));
        Assert.True(Seq(apply, "--skip-ui-layer", "false"));
        Assert.True(Seq(apply, "--install-path", @"D:\DCS World"));
        Assert.True(Seq(apply, "--saved-games", @"C:\Users\Pilot\Saved Games\DCS"));
        Assert.True(vm.DcsStripDefaults);
        Assert.True(fake.StripEnabled);
    }

    [Fact]
    public async Task SwitchingStripOn_No_LeavesTheSwitchOff_AndRunsNothing()
    {
        StripFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.ConfirmHandler = _ => Task.FromResult(false);

        fake.Calls.Clear();
        vm.DcsStripDefaults = true;
        await WaitForAsync(() => !vm.DcsStripDefaults);
        Assert.False(vm.DcsStripDefaults);
        Assert.Empty(fake.Calls);
        Assert.False(fake.StripEnabled);
    }

    [Fact]
    public async Task DeviceFiles_Move_RebuildsTheCleanProfiles()
    {
        StripFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.ConfirmHandler = _ => Task.FromResult(true);

        fake.Calls.Clear();
        vm.DcsRemoveDeviceFiles = true;
        await WaitForAsync(() => fake.Calls.Any(c => Seq(c, "dcs", "refresh", "clean")));
        Assert.Contains(fake.Calls, c => Seq(c, "dcs", "device-luas", "remove"));
        IReadOnlyList<string> clean = fake.Calls.Single(c => Seq(c, "dcs", "refresh", "clean"));
        Assert.True(Seq(clean, "--headless-lua", "true"));
        Assert.True(vm.DcsRemoveDeviceFiles);
    }

    [Fact]
    public async Task Preview_ListsTheFiles_AndRestoreRunsBoth()
    {
        StripFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.ConfirmHandler = _ => Task.FromResult(true);

        await vm.PreviewDcsStripDefaultsCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => Seq(c, "dcs", "strip-defaults", "apply", "--dry-run"));
        Assert.True(vm.HasDcsDefaultsPreview);
        Assert.Equal(2, vm.DcsDefaultsPreviewFiles.Count);
        Assert.Contains("2 input files", vm.DcsDefaultsPreviewTitle);
        Assert.False(fake.StripEnabled);

        fake.StripEnabled = true;
        fake.Calls.Clear();
        await vm.RestoreDcsInstallDefaultsCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => Seq(c, "dcs", "strip-defaults", "restore"));
        Assert.Contains(fake.Calls, c => Seq(c, "dcs", "device-luas", "restore"));
        Assert.False(vm.DcsStripDefaults);
    }

    [Fact]
    public void Parser_ReadsTheEngineResult_AndTheStatusSaysWhatIsOn()
    {
        string json = "{\"Action\":\"status\",\"ProfileFiles\":10,\"Patched\":0,\"AlreadyPatched\":7,\"StripEnabled\":true,\"SkipUiLayer\":false,\"RemoveDeviceLuas\":true," +
                      "\"DeviceFilesInInstall\":2,\"DeviceFilesBackedUp\":40,\"NeedsReapply\":true,\"CleanProfilesStale\":false,\"DcsVersion\":\"2.9.30\",\"Files\":[\"a.lua\"],\"Errors\":[]}";
        Assert.True(MainWindowViewModel.TryReadDcsDefaults("noise\n" + json, out MainWindowViewModel.DcsDefaultsInfo info));
        Assert.True(info.StripEnabled && info.RemoveDeviceLuas && info.NeedsReapply);
        Assert.Equal(7, info.AlreadyPatched);
        string text = MainWindowViewModel.DescribeDcsDefaults(info);
        Assert.Contains("7 of 10", text);
        Assert.Contains("40 kept by JoyPro, 2 in DCS", text);
        Assert.Contains("DCS 2.9.30", text);
        Assert.False(MainWindowViewModel.TryReadDcsDefaults("{\"Games\":[]}", out _));
    }

    private static async Task<MainWindowViewModel> CreateVmAsync(StripFakeEngine fake)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
        Assert.True(vm.IsDcsInstalled);
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static bool Seq(IReadOnlyList<string> args, params string[] sequence)
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

    private sealed class StripFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool StripEnabled { get; set; }
        public bool SkipUiLayer { get; set; }
        public bool RemoveDeviceLuas { get; set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private string Output(IReadOnlyList<string> args)
        {
            if (Seq(args, "games", "installs"))
                return "{\"Games\":[{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"D:\\\\DCS World\",\"Source\":\"test\",\"ConfigPaths\":[]}]}";
            bool dry = args.Contains("--dry-run");
            bool stale = false;
            List<string> files = [];
            if (Seq(args, "strip-defaults", "apply"))
            {
                if (dry)
                    files.AddRange(["D:/DCS World/Mods/aircraft/A/Input/A/joystick/default.lua", "D:/DCS World/Config/Input/UiLayer/joystick/default.lua"]);
                else
                {
                    StripEnabled = true;
                    SkipUiLayer = Seq(args, "--skip-ui-layer", "true");
                }
            }
            else if (Seq(args, "strip-defaults", "restore"))
                StripEnabled = false;
            else if (Seq(args, "device-luas", "remove"))
            {
                RemoveDeviceLuas = true;
                stale = true;
            }
            else if (Seq(args, "device-luas", "restore"))
            {
                RemoveDeviceLuas = false;
                stale = true;
            }
            if (!Seq(args, "strip-defaults") && !Seq(args, "device-luas"))
                return "{}";
            int patched = StripEnabled ? 218 : 0;
            return "{\"Action\":\"x\",\"ProfileFiles\":218,\"Patched\":0,\"AlreadyPatched\":" + patched + ",\"StripEnabled\":" + Lower(StripEnabled) +
                   ",\"SkipUiLayer\":" + Lower(SkipUiLayer) + ",\"RemoveDeviceLuas\":" + Lower(RemoveDeviceLuas) + ",\"CleanProfilesStale\":" + Lower(stale) +
                   ",\"DeviceFilesInInstall\":" + (RemoveDeviceLuas ? 0 : 554) + ",\"DeviceFilesBackedUp\":" + (RemoveDeviceLuas ? 554 : 0) +
                   ",\"NeedsReapply\":false,\"DcsVersion\":\"2.9.30.28718\",\"Files\":[" + string.Join(",", files.Select(f => "\"" + f + "\"")) + "],\"Errors\":[]}";
        }

        private static string Lower(bool value) => value ? "true" : "false";
    }
}
