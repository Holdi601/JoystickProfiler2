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

// 2.6.5 tester report (Speed of heat) with the real engine on a temp profile: the axis response's "Applies to" list
// (ticking the one aircraft that used DCS's curve ticked DCS and saved DCS's curve; "uses All games" read as if All
// games were ticked; the curve detent mode makes could be edited and saved over, which switched detent mode off) and
// Record's message (the device's full name with its instance GUID instead of its name in the list).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class SpeedReport266Tests
{
    private const string Throttle = "THROTTLE - THRUST - JETS";

    private static string Plane(string id) => "plane:DCS:" + id;

    private static AxisTuningScopeItem Row(MainWindowViewModel vm, string scope) => vm.AxisTuningScopes.Single(s => s.Scope == scope);

    private static async Task SaveAsync(AxisTuningAppliesToTests.Setup setup, MainWindowViewModel vm)
    {
        await Task.Delay(2500);
        int before = setup.Versions;
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.Equal(before + 1, setup.Versions);
    }

    [Fact]
    public async Task TickingTheOnlyAircraftThatUsesTheGameCurve_SavesItsOwnCurve_NotTheGames()
    {
        await using AxisTuningAppliesToTests.Setup? setup = await AxisTuningAppliesToTests.CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();

        // Four of five get a curve each; SA342 Pilot alone still uses DCS's curve.
        Row(vm, Plane("SA342 Pilot")).IsChecked = false;
        await AxisTuningAppliesToTests.SettleAsync(vm);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "12";
        await SaveAsync(setup, vm);
        Assert.False(Row(vm, Plane("SA342 Pilot")).IsOwn);

        // Untick the four, tick SA342 Pilot: one aircraft, not DCS.
        foreach (string plane in AxisTuningAppliesToTests.Helicopters.Where(p => p != "SA342 Pilot"))
            Row(vm, Plane(plane)).IsChecked = false;
        Assert.Empty(vm.AxisTuningTicked);
        Row(vm, Plane("SA342 Pilot")).IsChecked = true;
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.False(Row(vm, "game:DCS").IsChecked);
        Assert.Equal([Plane("SA342 Pilot")], vm.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.Equal("Save for the ticked aircraft (1 of 5)", vm.AxisTuningSaveText);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "7";
        await SaveAsync(setup, vm);
        Dictionary<string, string> scopes = await setup.ScopesAsync(AxisTuningAppliesToTests.Collective);
        Assert.Contains("dz=0.07;", scopes[Plane("SA342 Pilot")]);
        Assert.False(scopes.ContainsKey("game:DCS"));

        // All five ticked one by one: that is DCS's curve for the aircraft listed here, said as such.
        foreach (string plane in AxisTuningAppliesToTests.Helicopters)
            Row(vm, Plane(plane)).IsChecked = true;
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.True(Row(vm, "game:DCS").IsChecked);
        Assert.Equal("Save stores it for DCS: all 5 aircraft listed here.", vm.AxisTuningSavePlanText);
    }

    [Fact]
    public async Task TheCurveForAllGames_IsCalledTheDefaultCurve()
    {
        await using AxisTuningAppliesToTests.Setup? setup = await AxisTuningAppliesToTests.CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();

        Assert.Equal("Default curve", Row(vm, "base").Label);
        Assert.False(Row(vm, "base").IsChecked);
        Assert.All(AxisTuningAppliesToTests.Helicopters, p => Assert.Equal("uses the default curve", Row(vm, Plane(p)).UsesText));
        Assert.Equal("uses the default curve", Row(vm, "game:DCS").UsesText);
        Assert.Contains("only this relation", Row(vm, "game:DCS").UsesTip);
        Assert.DoesNotContain(vm.AxisTuningScopes, r => r.UsesText.Contains("All games", StringComparison.Ordinal));
        Assert.Equal("Shown: the curve DCS uses now (uses the default curve).", vm.AxisTuningStatus);

        vm.TickAxisTuningRows(["base"]);
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.Equal("Save as default curve", vm.AxisTuningSaveText);
        Assert.Equal("Shown: the default curve.", vm.AxisTuningStatus);
    }

    [Fact]
    public async Task InDetentMode_TheBentCurveIsShown_AndCannotBeSavedOver()
    {
        await using AxisTuningAppliesToTests.Setup? setup = await AxisTuningAppliesToTests.CreateAsync();
        if (setup == null)
            return;
        // A jet throttle with detent mode on and a calibrated detent at 80 % of the travel.
        await setup.RunAsync("relations", "add", Throttle, "--axis", "--node", "DCS:a2004cdnil:A-10C|F-16C_50|FA-18C_hornet", "--device", AxisTuningAppliesToTests.Throttle, "--input", "JOY_RX");
        await setup.RunAsync("binds", "detent", "set", Throttle, "--on");
        await setup.RunAsync("binds", "detent", "calibrate", Throttle, "--idle", "0", "--detent", "0.8", "--source", "wizard");
        MainWindowViewModel vm = await setup.OpenAsync(Throttle);

        Assert.Equal("game:DCS", vm.SelectedAxisTuningScope!.Scope);
        Assert.Contains(vm.AxisTuningScopes, r => r.IsAircraft && r.UsesText == "uses the default curve, bent at the detent");
        Assert.True(vm.AxisTuningDetentLocked);
        Assert.False(vm.AxisTuningEditable);
        Assert.False(vm.CanSaveAxisTuning);
        Assert.StartsWith("Shown: the curve detent mode makes for ", vm.AxisTuningStatus);
        Assert.Null(vm.AxisTuningHandles);

        // A save (a script, a shortcut) writes nothing: a DCS curve would switch detent mode off for these aircraft.
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.DoesNotContain((await setup.ScopesAsync(Throttle)).Keys, k => k.StartsWith("game:", StringComparison.Ordinal) || k.StartsWith("plane:", StringComparison.Ordinal));

        // The default curve (the one detent mode bends) stays editable.
        vm.TickAxisTuningRows(["base"]);
        await AxisTuningAppliesToTests.SettleAsync(vm);
        Assert.False(vm.AxisTuningDetentLocked);
        Assert.True(vm.CanSaveAxisTuning);
    }

    [Fact]
    public async Task Record_NamesTheDeviceAsTheListDoes_NotWithItsInstanceGuid()
    {
        await using AxisTuningAppliesToTests.Setup? setup = await AxisTuningAppliesToTests.CreateAsync();
        if (setup == null)
            return;
        await setup.RunAsync("devices", "alias", "set", AxisTuningAppliesToTests.Throttle, "THROTTLE");
        try
        {
            MainWindowViewModel vm = new(setup.Client, new FixedCapture(AxisTuningAppliesToTests.Throttle, "JOY_RY"), loadPersistedUiSettings: false)
            {
                EnginePath = setup.Engine,
                ProfilePath = setup.Profile
            };
            setup.Vms.Add(vm);
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Binds;
            vm.SelectedBind = vm.Binds.First(b => b.RelationName == AxisTuningAppliesToTests.Collective);
            DeviceAliases.Set(AxisTuningAppliesToTests.Throttle, "THROTTLE");
            await Task.Delay(300);
            vm.RecordTimeoutMs = "5000";
            await vm.RecordNextInputCommand.ExecuteAsync(null);

            // The test device is not connected: the list says so after its name, as the page does.
            Assert.StartsWith("Bound '" + AxisTuningAppliesToTests.Collective + "' to THROTTLE ", vm.RecordStatusText);
            Assert.EndsWith(" JOY_RY.", vm.RecordStatusText);
            Assert.DoesNotContain("{", vm.RecordStatusText);
            Assert.DoesNotContain("VPC Collective", vm.RecordStatusText);
            Assert.StartsWith("Bound " + AxisTuningAppliesToTests.Collective + " to THROTTLE ", vm.StatusText);
            Assert.DoesNotContain("{", vm.StatusText);
        }
        finally
        {
            DeviceAliases.Set(AxisTuningAppliesToTests.Throttle, null);
        }
    }

    private sealed class FixedCapture(string device, string input) : IJoystickCaptureService
    {
        public string LastDiagnostics => "fixed";

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<JoystickCaptureService.CaptureResult?>(new JoystickCaptureService.CaptureResult { Device = device, Input = input });

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
