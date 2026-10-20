using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Device aliases (docs/devices.md "Device names"): engine devices alias list/set/clear on a real profile, the alias
// moving with its device to a new instance GUID, and the display helper every view uses (alias or real name by the
// setting, the real name in the tooltip). The devices here have names of their own, so other tests never see them.
[Collection("DeviceAliases")]
public sealed class DeviceAliasTests : IDisposable
{
    private const string OldPanel = "Alias Test Panel {0000AAAA-0000-4000-8000-0000000000A1}";
    private const string NewPanel = "Alias Test Panel {0000BBBB-0000-4000-8000-0000000000B2}";

    private readonly string root = Path.Combine(Path.GetTempPath(), "jpa" + Guid.NewGuid().ToString("N")[..8]);

    public DeviceAliasTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        DeviceAliases.ShowAliases = true;
        DeviceAliases.Set(OldPanel, null);
        DeviceAliases.Set(NewPanel, null);
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Engine_SetsListsAndClearsTheAlias_AndTheAliasMovesWithTheDevice()
    {
        string profile = Path.Combine(root, "p.pr0file");
        string devices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(devices, NewPanel + "\t3344\t0259\tAlias Test Panel\r\n");

        Assert.Equal(0, Engine(devices, "--profile", profile, "devices", "alias", "set", OldPanel, "Left panel").Code);
        Assert.Equal([(OldPanel, "Left panel")], List(devices, profile));

        // A layout template's name (what layout suggest kept there before) is no alias.
        Assert.Equal(0, Engine(devices, "--profile", profile, "devices", "alias", "set", NewPanel, "Picture Missing").Code);
        Assert.Equal([(OldPanel, "Left panel")], List(devices, profile));
        Assert.Equal(0, Engine(devices, "--profile", profile, "devices", "alias", "clear", NewPanel).Code);

        // The device came back with a new instance GUID: the alias goes along.
        (int code, string output) = Engine(devices, "--profile", profile, "--text", "devices", "exchange", OldPanel, NewPanel);
        Assert.True(code == 0, output);
        Assert.Equal([(NewPanel, "Left panel")], List(devices, profile));

        Assert.Equal(0, Engine(devices, "--profile", profile, "devices", "alias", "clear", NewPanel).Code);
        Assert.Empty(List(devices, profile));
        Assert.NotEqual(0, Engine(devices, "--profile", profile, "devices", "alias", "set", NewPanel).Code);
    }

    [Fact]
    public void DisplayHelper_ShowsTheAliasOrTheRealNameBySetting()
    {
        DeviceAliases.Set(OldPanel, "Left panel");
        Assert.Equal("Left panel", DisplayText.ShortDevice(OldPanel));
        Assert.Contains("alias of " + OldPanel, DisplayText.DeviceTip(OldPanel));
        Assert.StartsWith("Left panel", DisplayText.BindLabel(OldPanel, "JOY_BTN5", null));
        Assert.Equal("Alias Test Panel", DisplayText.RealShortDevice(OldPanel));

        DeviceAliases.ShowAliases = false;
        Assert.Equal("Alias Test Panel", DisplayText.ShortDevice(OldPanel));
        Assert.Equal(OldPanel, DisplayText.DeviceTip(OldPanel));
        DeviceAliases.ShowAliases = true;

        // Windows gave the device a new GUID and the old one is not connected: its alias still shows.
        Func<string, bool>? connected = DeviceAliases.IsConnected;
        try
        {
            DeviceAliases.IsConnected = d => d == NewPanel;
            Assert.Equal("Left panel", DisplayText.ShortDevice(NewPanel));
            // Both connected (two alike panels): the other one keeps its own name.
            DeviceAliases.IsConnected = _ => true;
            Assert.Equal("Alias Test Panel", DisplayText.ShortDevice(NewPanel));
        }
        finally
        {
            DeviceAliases.IsConnected = connected;
        }
    }

    [Fact]
    public async Task LayoutPage_NamesTheDevice_CardsFollow_AndTheSettingSwitchesBack()
    {
        NoviceGuiTests.NoviceFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(root, "vm.pr0file")
        };
        File.WriteAllText(vm.ProfilePath, "{}");
        DeviceCardItem card = new(OldPanel, string.Empty, string.Empty, "panel", 0, 32, 0, connected: true, inProfile: true);
        vm.DeviceCards.Add(card);

        Assert.True(await vm.SetDeviceAliasAsync(OldPanel, "Left panel"));
        Assert.Contains(fake.Calls, c => c.Skip(c.ToList().IndexOf("devices")).SequenceEqual(["devices", "alias", "set", OldPanel, "Left panel"]));
        Assert.Equal("Left panel", card.Title);
        Assert.True(card.ShowsDeviceName);
        Assert.Contains("alias of", card.Tooltip);

        vm.ShowDeviceAliases = false;
        Assert.False(DeviceAliases.ShowAliases);
        Assert.Equal("Alias Test Panel", card.Title);
        vm.ShowDeviceAliases = true;

        Assert.True(await vm.SetDeviceAliasAsync(OldPanel, string.Empty));
        Assert.Contains(fake.Calls, c => c.Skip(c.ToList().IndexOf("devices")).SequenceEqual(["devices", "alias", "clear", OldPanel]));
        Assert.Equal("Alias Test Panel", card.Title);
    }

    private List<(string Device, string Alias)> List(string devices, string profile)
    {
        (int code, string output) = Engine(devices, "--profile", profile, "devices", "alias", "list");
        Assert.True(code == 0, output);
        using JsonDocument doc = JsonDocument.Parse(output.Trim());
        return doc.RootElement.GetProperty("Aliases").EnumerateArray()
            .Select(e => (e.GetProperty("Device").GetString()!, e.GetProperty("Alias").GetString()!)).ToList();
    }

    private (int Code, string Output) Engine(string devices, params string[] args)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        Assert.True(File.Exists(engine), "Build the engine first: " + engine);
        ProcessStartInfo info = new(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(root, "data");
        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "engine " + string.Join(" ", args) + " did not finish.");
        string output = stdout + stderr.Result;
        // "Auto-saved profile: ..." follows the JSON of a changing command.
        int saved = output.IndexOf("Auto-saved profile", StringComparison.Ordinal);
        return (process.ExitCode, saved >= 0 ? output[..saved] : output);
    }
}
