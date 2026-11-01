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

// 2.5.1 bug (sandbox log of the user's set): "VKB Gunfighter sits on vJoy Device", "vJoy Device sits on L-VPC Stick
// WarBRD", "VPforce Rhino sits on L-VPC Stick WarBRD". The real set: the VKB grip sits on the VPforce Rhino FFB base, the
// WarBRD is a separate (left) stick. Rules (docs/hud.md "Stick axes"): a virtual device (vJoy, the forwarder's tunnel
// device) never is a base and never sits on one; a base (kind "base" or one that takes grips, the Rhino) never sits on
// one; among the candidates a base comes before the fewest buttons.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudStickBaseTests
{
    internal const string WarBrd = "L-VPC Stick WarBRD {42CE9E60-0A3F-11f0-8009-444553540000}";
    internal const string Vkb = "VKB-Sim Gunfighter Modern Combat Pro Twist {42CF1390-0A3F-11f0-8011-444553540000}";
    internal const string Rhino = "VPforce Rhino FFB Joystick {AD5168C0-B52F-11f1-8001-444553540000}";
    internal const string VJoy = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";

    private static readonly string[] Axes8 = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    private static string AxesJson(IEnumerable<string> inputs) =>
        "[" + string.Join(",", inputs.Select(i => "{\"Input\":\"" + i + "\",\"Control\":\"\",\"Label\":\"" + i.Replace("JOY_", string.Empty) + "\"}")) + "]";

    // "hud devices" as the engine reported the user's four sticks (gui-20261101.log run; names, VID:PID, kinds, hands,
    // buttons, hats and axes as reported; the Rhino without a picked grip has the catalog's grip choices).
    internal static string UserSticksJson =>
        "{\"Devices\":[" +
        "{\"Device\":\"" + VJoy + "\",\"Name\":\"vJoy - Virtual Joystick\",\"VidPid\":\"1234:BEAD\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"stick\",\"Hand\":\"\",\"Image\":\"\",\"Buttons\":32,\"Hats\":4," +
        "\"Axes\":" + AxesJson(Axes8) + ",\"Labels\":{},\"GripChoices\":[],\"Layout\":null}," +
        "{\"Device\":\"" + Vkb + "\",\"Name\":\"VKB-Sim Gunfighter Modern Combat Pro Twist\",\"VidPid\":\"231D:0125\",\"Model\":\"VKB Gunfighter Modern Combat Grip Pro (MCG Pro, with Twist)\",\"ModelId\":\"vkb-gunfighter-mcg-pro\",\"Kind\":\"stick\",\"Hand\":\"right\",\"Image\":\"\",\"Buttons\":128,\"Hats\":2," +
        "\"Axes\":" + AxesJson(Axes8) + ",\"Labels\":{\"JOY_BTN1\":\"Trigger\"},\"GripChoices\":[],\"Layout\":null}," +
        "{\"Device\":\"" + Rhino + "\",\"Name\":\"Rhino FFB Joystick\",\"VidPid\":\"FFFF:2055\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"stick\",\"Hand\":\"\",\"Image\":\"\",\"Buttons\":64,\"Hats\":4," +
        "\"Axes\":" + AxesJson(Axes8.Take(7)) + ",\"Labels\":{}," +
        "\"GripChoices\":[{\"Id\":\"vpforce-rhino-vkb-adapter\",\"Name\":\"VKB grip (adapter)\"},{\"Id\":\"vpforce-rhino-tm-warthog-grip\",\"Name\":\"Thrustmaster Warthog grip\"}],\"Layout\":null}," +
        "{\"Device\":\"" + WarBrd + "\",\"Name\":\"L-VPC Stick WarBRD\",\"VidPid\":\"3344:80CB\",\"Model\":\"Virpil VPC Constellation ALPHA-L (Configurator 2.0 layout)\",\"ModelId\":\"virpil-alpha-l-v2\",\"Kind\":\"stick\",\"Hand\":\"left\",\"Image\":\"\",\"Buttons\":31,\"Hats\":0," +
        "\"Axes\":" + AxesJson(["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_SLIDER1"]) + ",\"Labels\":{},\"GripChoices\":[],\"Layout\":null}" +
        "],\"Warnings\":[]}";

    internal sealed class UserSticksEngine : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = arguments.Contains("hud") && arguments.Contains("devices") ? UserSticksJson : "{}";
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, Arguments = arguments.ToArray() });
        }
    }

    private static HudDevice Device(string device, string name, string kind, string hand, int buttons, int hats, IEnumerable<string> axes, string vidPid,
        IEnumerable<(string, string)>? grips = null, string model = "") =>
        new(device, name, model, kind, buttons, hats, axes.Select(a => (a, string.Empty, a)), new Dictionary<string, string>(), grips ?? [], hand) { VidPid = vidPid };

    private static List<HudDevice> UserSticks(string vkbHand = "right", string warBrdHand = "left", bool rhinoGrips = true, string vJoyVidPid = "1234:BEAD") =>
    [
        Device(VJoy, "vJoy - Virtual Joystick", "stick", "", 32, 4, Axes8, vJoyVidPid),
        Device(Vkb, "VKB-Sim Gunfighter Modern Combat Pro Twist", "stick", vkbHand, 128, 2, Axes8, "231D:0125", model: "VKB Gunfighter Modern Combat Grip Pro (MCG Pro, with Twist)"),
        Device(Rhino, "Rhino FFB Joystick", "stick", "", 64, 4, Axes8.Take(7), "FFFF:2055", rhinoGrips ? [("vpforce-rhino-vkb-adapter", "VKB grip (adapter)")] : null),
        Device(WarBrd, "L-VPC Stick WarBRD", "stick", warBrdHand, 31, 0, ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_SLIDER1"], "3344:80CB",
            model: "Virpil VPC Constellation ALPHA-L (Configurator 2.0 layout)")
    ];

    private static HudDevice? BaseOf(List<HudDevice> devices, string device) =>
        MainWindowViewModel.PairedHudBase(devices.Single(d => d.Device == device), devices);

    [Fact]
    public void UserSet_VkbSitsOnTheRhino_NothingOnVJoy_RhinoAndWarBrdAndVJoyGetNoBase()
    {
        List<HudDevice> devices = UserSticks();
        Assert.True(devices.Single(d => d.Device == VJoy).IsVirtual);
        Assert.True(devices.Single(d => d.Device == Rhino).IsBase);
        Assert.Same(devices.Single(d => d.Device == Rhino), BaseOf(devices, Vkb));
        Assert.Null(BaseOf(devices, Rhino));
        Assert.Null(BaseOf(devices, VJoy));
        Assert.Null(BaseOf(devices, WarBrd));
    }

    // Without hands from the catalog the Rhino (a base that takes grips) still comes before the WarBRD's fewer buttons;
    // a vJoy device found by its name alone (no VID:PID) is still virtual.
    [Theory]
    [InlineData("", "left")]
    [InlineData("right", "")]
    [InlineData("", "")]
    public void UserSet_WithoutHands_TheBaseWins_AndVJoyIsVirtualByName(string vkbHand, string warBrdHand)
    {
        List<HudDevice> devices = UserSticks(vkbHand, warBrdHand, vJoyVidPid: "");
        Assert.True(devices.Single(d => d.Device == VJoy).IsVirtual);
        Assert.Same(devices.Single(d => d.Device == Rhino), BaseOf(devices, Vkb));
        Assert.Null(BaseOf(devices, Rhino));
        Assert.Null(BaseOf(devices, VJoy));
    }

    [Fact]
    public void VirtualDevices_AreRecognised()
    {
        Assert.True(HudDevice.LooksVirtual("vJoy Device {89D63500-B53F-11f1-8002-444553540000}", "vJoy - Virtual Joystick", ""));
        Assert.True(HudDevice.LooksVirtual("Some Device {x}", "Some Device", "1234:bead"));
        Assert.True(HudDevice.LooksVirtual("ViGEm Bus Device", "", ""));
        Assert.False(HudDevice.LooksVirtual(Rhino, "Rhino FFB Joystick", "FFFF:2055"));
        Assert.False(HudDevice.LooksVirtual(Vkb, "VKB-Sim Gunfighter Modern Combat Pro Twist", "231D:0125"));
        Assert.False(HudDevice.LooksVirtual(WarBrd, "L-VPC Stick WarBRD", "3344:80CB"));
    }

    // The whole way: the engine's devices read for the HUD, the log's pairing and what Automatic says; the aircraft
    // widget's automatic stick is the right-hand grip, never the vJoy device.
    [Fact]
    public async Task Hud_UserSet_PairsAsTheRealSet()
    {
        MainWindowViewModel vm = new(new UserSticksEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        await vm.LoadHudDevicesCommand.ExecuteAsync(null);
        HudDevice Of(string device) => vm.HudWidgets.Single(w => w.Device?.Device == device).Device!;
        Assert.Equal("1234:BEAD", Of(VJoy).VidPid);
        Assert.Same(Of(Rhino), Of(Vkb).PairedBase);
        Assert.Null(Of(Rhino).PairedBase);
        Assert.Null(Of(VJoy).PairedBase);
        Assert.Null(Of(WarBrd).PairedBase);
        Assert.DoesNotContain(vm.HudWidgets.Where(w => w.Device != null), w => ReferenceEquals(w.Device!.PairedBase, Of(VJoy)));
        vm.SelectedHudWidget = vm.HudWidgets.Single(w => w.Device?.Device == Rhino);
        Assert.DoesNotContain("come from", vm.HudAxisAutomaticText);
        vm.SelectedHudWidget = vm.HudWidgets.Single(w => w.Device?.Device == Vkb);
        Assert.Contains("Rhino", vm.HudAxisAutomaticText);
    }
}
