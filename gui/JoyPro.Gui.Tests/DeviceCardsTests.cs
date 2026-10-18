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

// The device cards of the Start page ("Your devices") and Settings > Devices with the devices of a real PC (the engine's
// answers of the user's PC, 2026-10-11): two Virpil devices, VKB, MFG pedals, a WinWing panel, a VPforce Rhino FFB base,
// vJoy, and a Wooting keyboard with five game-controller interfaces, plus the driver entries Windows lists. One card per
// real device with the device reference's model and drawing; no card for driver entries; the keyboard as one line
// unless the profile uses it; vJoy marked virtual; the Rhino shows its base until a grip is picked, then the grip.
[SupportedOSPlatform("windows")]
public sealed class DeviceCardsTests : IDisposable
{
    private const string Throttle = "LEFT VPC Throttle MT-50 CM {42CEC570-0A3F-11f0-800B-444553540000}";
    private const string WarBrd = "L-VPC Stick WarBRD {42CE9E60-0A3F-11f0-8009-444553540000}";
    private const string Vkb = "VKB-Sim Gunfighter Modern Combat Pro Twist {42CF1390-0A3F-11f0-8011-444553540000}";
    private const string Rhino = "VPforce Rhino FFB Joystick {AD5168C0-B52F-11f1-8001-444553540000}";
    private const string VJoy = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";
    private const string Pedals = "MFG Crosswind V2 {42CE2930-0A3F-11f0-8001-444553540000}";
    private const string Panel = "WINWING F18 TAKEOFF PANEL {42CE5040-0A3F-11f0-8004-444553540000}";
    private static readonly string[] Wooting =
    [
        "Wooting Two HE (ARM) {42CE2930-0A3F-11f0-8002-444553540000}", "Wooting Two HE (ARM) {42CF1390-0A3F-11f0-800E-444553540000}",
        "Wooting Two HE (ARM) {42CF3AA0-0A3F-11f0-8012-444553540000}", "Wooting Two HE (ARM) {42CFAFD0-0A3F-11f0-8018-444553540000}",
        "Wooting Two HE (ARM) {42CFFDF0-0A3F-11f0-801D-444553540000}"
    ];

    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "device-cards-" + Guid.NewGuid().ToString("N"));

    // The tests' JOYPRO_DATA_DIR stays as it is (the view model's settings folder is read from it once per run); the
    // grips picked here are removed before and after each test.
    private static string GripFile => Path.Combine(DeviceReferenceService.DataFolder(), "grips.json");

    public DeviceCardsTests()
    {
        Directory.CreateDirectory(temp);
        if (File.Exists(GripFile))
            File.Delete(GripFile);
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
    }

    public void Dispose()
    {
        if (File.Exists(GripFile))
            File.Delete(GripFile);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Cards_OnePerRealDevice_WithTheReferenceModel_NoDriverEntries_KeyboardAsOneLine()
    {
        RealPcFakeEngine fake = new() { ProfileDevices = [Throttle, Vkb] };
        MainWindowViewModel vm = CreateVm(fake);

        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);

        List<DeviceCardItem> connected = vm.DeviceCards.Where(c => c.Connected).ToList();
        // MFG, WinWing, vJoy, VKB, Rhino, both Virpil devices and the Wooting keyboard (its gamepad interface); no
        // driver entry, the keyboard's other interfaces as one line.
        Assert.Equal(8, connected.Count);
        DeviceCardItem keyboard = Assert.Single(vm.DeviceCards, c => c.Device.Contains("Wooting", StringComparison.Ordinal));
        Assert.Equal(Wooting[0], keyboard.Device);
        Assert.Equal("class.keyboard", keyboard.Schematic?.Shape);
        Assert.DoesNotContain(vm.DeviceCards, c => !c.Device.Contains('{'));
        Assert.DoesNotContain(vm.DeviceCards, c => c.Device.StartsWith("vJoy Driver", StringComparison.Ordinal) || c.Device.StartsWith("HID-compliant", StringComparison.Ordinal));
        Assert.Equal("Wooting Two HE (ARM) (4 interfaces)", vm.OtherDevicesText);
        Assert.True(vm.HasOtherDevices);

        // The Virpil devices with the reference's model and kind; the throttle's 60 parts would not make a
        // recognizable drawing, so the card shows the throttle outline (no made-up layout of parts).
        DeviceCardItem throttle = connected.Single(c => c.Device == Throttle);
        Assert.Equal("virpil-mongoost-50cm-throttle", throttle.ModelId);
        Assert.Contains("MongoosT-50CM", throttle.Title, StringComparison.Ordinal);
        Assert.Equal("LEFT VPC Throttle MT-50 CM", throttle.DeviceName);
        Assert.Equal("Throttle", throttle.KindLabel);
        Assert.Equal("class.throttle", throttle.Schematic?.Shape);
        // JoyPro's own drawing of the model when it ships one, else the class outline.
        Assert.Contains(throttle.PictureSource, new[] { "JoyPro line drawing", "Drawing: Throttle" });
        Assert.True(throttle.InProfile);
        // The WarBRD base does not say which grip is on it: the base, "Which grip?".
        DeviceCardItem stick = connected.Single(c => c.Device == WarBrd);
        Assert.Equal("virpil-warbrd-base", stick.ModelId);
        Assert.True(stick.NeedsGrip);
        Assert.Equal("Base", stick.KindLabel);
        Assert.False(stick.InProfile);
        Assert.Equal("Pedals", connected.Single(c => c.Device == Pedals).KindLabel);
        Assert.Equal("Panel", connected.Single(c => c.Device == Panel).KindLabel);
        // JoyPro's own drawing (the line drawing of its photo when one ships, else the schematic drawing).
        Assert.Contains(connected.Single(c => c.Device == Panel).PictureSource, new[] { "JoyPro line drawing", "JoyPro drawing" });

        // vJoy: a card marked virtual, the outline of a virtual device.
        DeviceCardItem vjoy = connected.Single(c => c.Device == VJoy);
        Assert.True(vjoy.IsVirtual);
        Assert.Equal("Virtual", vjoy.KindLabel);
        Assert.Equal("class.virtual", vjoy.Schematic?.Shape);

        // The Rhino without a grip set: the FFB base, not one of its 17 grips.
        DeviceCardItem rhino = connected.Single(c => c.Device == Rhino);
        Assert.Equal("vpforce-rhino", rhino.ModelId);
        Assert.Equal("VPforce Rhino FFB Base", rhino.Title);
        Assert.Equal("FFB base", rhino.KindLabel);
        Assert.True(rhino.NeedsGrip);

        // Sticks first, the virtual device after the real ones.
        Assert.True(connected.IndexOf(vjoy) > connected.IndexOf(connected.Single(c => c.Device == Panel)));
        Assert.True(connected.IndexOf(stick) < connected.IndexOf(throttle));

        // The Start page shows the same cards at once (startup and Refresh both read the devices through this command).
        Assert.Equal(connected, vm.DashboardDevices.ToList());
        Assert.True(vm.HasDashboardDevices);
        Assert.Equal(8, vm.ConnectedDeviceCount);
    }

    [Fact]
    public async Task Cards_TheGripPickedForTheRhino_IsShownAndSentToTheEngine()
    {
        RealPcFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        Assert.Equal("VPforce Rhino FFB Base", vm.DeviceCards.Single(c => c.Device == Rhino).Title);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--grip"));

        // The grip picked on the Layout page (kept by the device reference).
        DeviceReferenceService.SetGrip(Rhino, "vpforce-rhino-virpil-warbrd");
        fake.Calls.Clear();
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);

        DeviceCardItem rhino = vm.DeviceCards.Single(c => c.Device == Rhino);
        Assert.Equal("vpforce-rhino-virpil-warbrd", rhino.ModelId);
        Assert.Equal("Virpil WarBRD / WarBRD-D Grip on Rhino", rhino.Title);
        Assert.Equal("FFB stick", rhino.KindLabel);
        Assert.False(rhino.NeedsGrip);
        Assert.NotNull(rhino.Schematic);
        IReadOnlyList<string> hud = fake.Calls.Single(c => NoviceGuiTests.Seq(c, "hud", "devices"));
        Assert.True(NoviceGuiTests.Seq(hud, "--grip", Rhino + "=vpforce-rhino-virpil-warbrd"));

        // The Layout page shows the picked grip too (with the other grips of the base on offer).
        await vm.ShowDeviceReferenceForAsync(Rhino);
        Assert.Equal("vpforce-rhino-virpil-warbrd", vm.DeviceReference.Model?.Id);
        Assert.True(vm.DeviceReference.HasGripChoices);
        Assert.Equal("vpforce-rhino-virpil-warbrd", vm.DeviceReference.SelectedGrip?.Id);
    }

    [Fact]
    public async Task Cards_AKeyboardInterfaceTheProfileUses_GetsItsCard()
    {
        RealPcFakeEngine fake = new() { ProfileDevices = [Wooting[0], "Old Pedals {00000000-0000-0000-0000-0000000000a9}", "vJoy Driver"] };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);

        DeviceCardItem wooting = Assert.Single(vm.DeviceCards, c => c.Device.Contains("Wooting", StringComparison.Ordinal));
        Assert.Equal(Wooting[0], wooting.Device);
        Assert.True(wooting.InProfile);
        Assert.Equal("Wooting Two HE (ARM) (4 interfaces)", vm.OtherDevicesText);
        // A device of the profile that is not connected keeps its card; a driver entry of the profile gets none.
        DeviceCardItem old = vm.DeviceCards.Single(c => c.Device.StartsWith("Old Pedals", StringComparison.Ordinal));
        Assert.False(old.Connected);
        Assert.DoesNotContain(vm.DeviceCards, c => c.Device == "vJoy Driver");
        Assert.DoesNotContain(vm.DashboardDevices, c => !c.Connected);
    }

    [Fact]
    public async Task ClickOnACard_OpensTheLayoutPageWithTheDevice()
    {
        RealPcFakeEngine fake = new() { ProfileDevices = [Throttle] };
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(temp, "cards.pr0file");
        File.WriteAllText(vm.ProfilePath, "{}");
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        Assert.Equal(UiStage.Start, vm.CurrentStage);

        DeviceCardItem throttle = vm.DashboardDevices.Single(c => c.Device == Throttle);
        await vm.OpenDeviceCardInLayoutCommand.ExecuteAsync(throttle);
        for (int i = 0; i < 300 && vm.DeviceReference.DeviceName != Throttle; i++)
            await Task.Delay(10);

        Assert.Equal(UiStage.Layout, vm.CurrentStage);
        Assert.False(vm.IsCheatSheetsSection);
        Assert.Equal(Throttle, vm.DeviceReference.DeviceName);
        Assert.Equal("virpil-mongoost-50cm-throttle", vm.DeviceReference.Model?.Id);
        Assert.Equal(throttle.Title, vm.DeviceReference.Title);
    }

    [Fact]
    public void PseudoEntries_AreNoDevices()
    {
        Assert.True(MainWindowViewModel.IsPseudoDevice("HID-compliant game controller"));
        Assert.True(MainWindowViewModel.IsPseudoDevice("Microsoft PC-joystick driver"));
        Assert.True(MainWindowViewModel.IsPseudoDevice("vJoy Driver"));
        Assert.True(MainWindowViewModel.IsPseudoDevice("vJoy Device"));
        Assert.False(MainWindowViewModel.IsPseudoDevice(VJoy));
        Assert.False(MainWindowViewModel.IsPseudoDevice(Throttle));
    }

    private static MainWindowViewModel CreateVm(IEngineCliClient fake) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    // engine devices list and hud devices as on the user's PC (hud devices lists only DirectInput devices; the driver
    // entries and a GUID-less "vJoy Device" are added to the answer to show they get no card either).
    internal sealed class RealPcFakeEngine : IEngineCliClient
    {
        public List<string> ProfileDevices { get; init; } = [];

        public List<IReadOnlyList<string>> Calls { get; } = [];

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

        private static string[] ConnectedNames =>
        [
            "HID-compliant game controller", Throttle, WarBrd, Pedals, "Microsoft PC-joystick driver", "vJoy Device", VJoy, "vJoy Driver", Vkb, Rhino, Panel, .. Wooting
        ];

        private string Output(IReadOnlyList<string> args)
        {
            if (NoviceGuiTests.Seq(args, "devices", "list"))
            {
                return string.Join(Environment.NewLine, new[] { "Connected:" }.Concat(ConnectedNames.Select(n => "  - " + n))
                    .Concat(["Profile:"]).Concat(ProfileDevices.Select(n => "  - " + n)));
            }
            if (NoviceGuiTests.Seq(args, "hud", "devices"))
            {
                string rhinoGrip = string.Empty;
                for (int i = 0; i + 1 < args.Count; i++)
                {
                    if (args[i] == "--grip" && args[i + 1].StartsWith(Rhino + "=", StringComparison.Ordinal))
                        rhinoGrip = args[i + 1][(Rhino.Length + 1)..];
                }
                List<string> devices =
                [
                    Device(Pedals, "MFG Crosswind V2", "16D0:0A38", "MFG Crosswind V2 Rudder Pedals", "mfg-crosswind-v2", "pedals", 2, 0, ["JOY_X", "JOY_Y", "JOY_RZ"]),
                    Device(Wooting[0], "Wooting Two HE (ARM)", "31E3:1231", "", "", "other", 12, 1, ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1"]),
                    Device(Panel, "WINWING F18 TAKEOFF PANEL", "4098:BE04", "WinCtrl F/A-18 Takeoff Panel (PTO)", "winwing-f18-takeoff-panel", "panel", 38, 0, ["JOY_X", "JOY_Y"]),
                    Device(VJoy, "vJoy - Virtual Joystick", "1234:BEAD", "", "", "stick", 32, 4, ["JOY_X", "JOY_Y", "JOY_Z"]),
                    Device(Vkb, "VKB-Sim Gunfighter Modern Combat Pro Twist", "231D:0125", "VKB Gunfighter Modern Combat Grip Pro (MCG Pro, with Twist)", "vkb-gunfighter-mcg-pro", "stick", 128, 2, ["JOY_X", "JOY_Y", "JOY_RZ"]),
                    rhinoGrip.Length > 0
                        ? Device(Rhino, "Rhino FFB Joystick", "FFFF:2055", "Virpil WarBRD / WarBRD-D Grip on Rhino", rhinoGrip, "stick", 64, 4, ["JOY_X", "JOY_Y"])
                        : Device(Rhino, "Rhino FFB Joystick", "FFFF:2055", "", "", "stick", 64, 4, ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1"],
                            "\"GripChoices\":[{\"Id\":\"vpforce-rhino-tm-warthog-grip\",\"Name\":\"Thrustmaster Warthog Grip on Rhino\",\"Image\":\"\"},{\"Id\":\"vpforce-rhino-virpil-warbrd\",\"Name\":\"Virpil WarBRD / WarBRD-D Grip on Rhino\",\"Image\":\"\"}]"),
                    Device(Throttle, "LEFT VPC Throttle MT-50 CM", "03EB:2054", "Virpil VPC MongoosT-50CM Throttle (first CM, MT-50 CM)", "virpil-mongoost-50cm-throttle", "throttle", 94, 0, ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"]),
                    Device(WarBrd, "L-VPC Stick WarBRD", "3344:80CB", "Virpil VPC Constellation ALPHA-L (Configurator 2.0 layout)", "virpil-alpha-l-v2", "stick", 31, 0, ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ"]),
                    Device("vJoy Device", "vJoy Device", "", "", "", "stick", 0, 0, []),
                    Device("HID-compliant game controller", "HID-compliant game controller", "", "", "", "other", 0, 0, [])
                ];
                devices.AddRange(Wooting.Skip(1).Select(w => Device(w, "Wooting Two HE (ARM)", "31E3:1231", "", "", "other", 0, 0, [])));
                return "{\"Devices\":[" + string.Join(",", devices) + "],\"Warnings\":[]}";
            }
            return "ok";
        }

        private static string Device(string device, string name, string vidPid, string model, string modelId, string kind, int buttons, int hats, string[] axes, string extra = "")
        {
            return "{\"Device\":\"" + device + "\",\"Name\":\"" + name + "\",\"VidPid\":\"" + vidPid + "\",\"Model\":\"" + model + "\",\"ModelId\":\"" + modelId +
                   "\",\"Kind\":\"" + kind + "\",\"Hand\":\"\",\"Generic\":false,\"Image\":\"\",\"Buttons\":" + buttons + ",\"Hats\":" + hats +
                   ",\"Axes\":[" + string.Join(",", axes.Select(a => "{\"Input\":\"" + a + "\",\"Control\":\"\",\"Label\":\"\"}")) + "],\"Labels\":{}" +
                   (extra.Length > 0 ? "," + extra : ",\"GripChoices\":[]") + ",\"Layout\":null}";
        }
    }
}
