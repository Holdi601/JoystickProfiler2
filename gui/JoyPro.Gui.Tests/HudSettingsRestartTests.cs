using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.1 bug (sandboxed live check): a hud.json with the page for OBS and the stream window on came up with both off,
// and the GUI's own hud.json had them off afterwards. Cause: the HUD page's design controls exist (hidden) from the
// window's start and bind to the look editor; a colour field writes its own spelling back (#38BDF8 -> #FF38BDF8), which
// counts as an edit and saved the HUD 0.5 s later, before hud.json was ever read: the defaults (outputs off, Transparent)
// replaced the file, and the HUD page and Start HUD then read those. Now nothing is saved before hud.json was read, and
// a hud.json that cannot be read is kept as hud.json.unreadable-<time> before anything is written.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudSettingsRestartTests
{
    // As PowerShell 5.1 writes it (ConvertTo-Json, Set-Content -Encoding UTF8): a BOM, wide indentation, a 2.5.0 widget.
    private static string PowerShellHudJson(int port) =>
        "{\r\n    \"Widgets\":  [\r\n                    {\r\n                        \"Id\":  \"header\",\r\n                        \"Kind\":  \"header\",\r\n" +
        "                        \"Device\":  \"\",\r\n                        \"X\":  40,\r\n                        \"Y\":  36,\r\n                        \"Scale\":  1,\r\n" +
        "                        \"Visible\":  true\r\n                    }\r\n                ],\r\n    \"Accent\":  \"#38BDF8\",\r\n    \"Style\":  \"Glass\",\r\n" +
        "    \"Opacity\":  1,\r\n    \"StreamBackground\":  \"#00FF00\",\r\n    \"OnScreen\":  true,\r\n    \"StreamBrowser\":  true,\r\n    \"StreamWindow\":  true,\r\n" +
        "    \"StreamPort\":  " + port + "\r\n}\r\n";

    private static string NewFolder(string name) => Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N"));

    private static MainWindowViewModel Vm() =>
        new(new TesterBatch250Tests.GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    private static void InSandbox(string name, Action<string> body)
    {
        string folder = NewFolder(name);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            Directory.CreateDirectory(AppInfo.SettingsFolder);
            body(AppInfo.SettingsFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool ReadBool(string file, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty(name).GetBoolean();
    }

    // Waits past the HUD's delayed save (0.5 s), pumping the UI thread meanwhile.
    private static void PastTheDelayedSave(HiddenWindow? window = null)
    {
        for (int i = 0; i < 15; i++)
        {
            window?.Settle();
            AvaloniaRenderThread.Pump();
            Thread.Sleep(100);
        }
    }

    [Fact]
    public void StreamOutputs_SurviveTheWindowsStart_AndARestart_AndStartHudStartsThePage()
    {
        InSandbox("hud-restart", settings =>
        {
            string file = Path.Combine(settings, "hud.json");
            int port = HudStreamerTests.FreePort();
            File.WriteAllText(file, PowerShellHudJson(port), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            byte[] before = File.ReadAllBytes(file);

            AvaloniaRenderThread.Run(() =>
            {
                // The app's start: the window with every page's controls bound, on the Start page; the HUD page not opened.
                MainWindowViewModel first = Vm();
                using (HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = first }, new Size(1280, 800)))
                {
                    PastTheDelayedSave(main);
                    Assert.Equal(before, File.ReadAllBytes(file));
                    first.OverlayPresenter = null;
                    first.HudStreamPresenter = null;
                }

                // A restart: a new view model, Start HUD (the Start page's button) without the HUD page opened first.
                MainWindowViewModel second = Vm();
                AvaloniaRenderThread.Wait(second.ToggleOverlayCommand.ExecuteAsync(null));
                try
                {
                    Assert.True(second.IsOverlayRunning);
                    Assert.True(second.HudStreamBrowser);
                    Assert.True(second.HudStreamWindow);
                    Assert.True(second.HudOnScreen);
                    Assert.Equal(port, (int)second.HudStreamPort);
                    Assert.Equal("#00FF00", second.HudStreamBackground);
                    HudStreamServer server = second.HudStreamServerForTests ?? throw new Xunit.Sdk.XunitException("no page server: " + second.HudStreamStatus);
                    Assert.True(server.IsRunning);
                    Assert.Equal(port, server.Port);
                    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
                    HttpResponseMessage page = AvaloniaRenderThread.Wait(http.GetAsync("http://127.0.0.1:" + port + "/hud"));
                    Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
                }
                finally
                {
                    second.ToggleOverlayCommand.Execute(null);
                    AvaloniaRenderThread.Pump();
                }
                Assert.Null(second.HudStreamServerForTests);

                // Saved by JoyPro now (the HUD was read): still on.
                second.SaveHudLayoutNow();
                Assert.True(ReadBool(file, "StreamBrowser"));
                Assert.True(ReadBool(file, "StreamWindow"));
                MainWindowViewModel third = Vm();
                AvaloniaRenderThread.Wait(third.LoadHudDevicesCommand.ExecuteAsync(null));
                Assert.True(third.HudStreamBrowser);
                Assert.True(third.HudStreamWindow);
            });
        });
    }

    // A change before hud.json was read (a control writing its value back) never writes the defaults over the file.
    [Fact]
    public void NothingIsSaved_BeforeHudJsonWasRead()
    {
        InSandbox("hud-early-save", settings =>
        {
            string file = Path.Combine(settings, "hud.json");
            File.WriteAllText(file, PowerShellHudJson(8765), new UTF8Encoding(true));
            byte[] before = File.ReadAllBytes(file);
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = Vm();
                vm.HudLookEditor.Accent = "#FF38BDF8";
                vm.HudMaxFps = 60;
                vm.SaveHudLayoutNow();
                PastTheDelayedSave();
                Assert.Equal(before, File.ReadAllBytes(file));

                // Read now: the file's outputs, and a change is saved.
                AvaloniaRenderThread.Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
                Assert.True(vm.HudStreamBrowser);
                vm.HudStreamPort = 8800;
                vm.SaveHudLayoutNow();
                Assert.True(ReadBool(file, "StreamBrowser"));
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                Assert.Equal(8800, doc.RootElement.GetProperty("StreamPort").GetInt32());
            });
        });
    }

    // A hud.json that cannot be read is kept as hud.json.unreadable-<time> before JoyPro writes its own.
    [Fact]
    public void UnreadableHudJson_IsKept_BeforeItIsReplaced()
    {
        InSandbox("hud-unreadable", settings =>
        {
            string file = Path.Combine(settings, "hud.json");
            const string broken = "{ \"StreamBrowser\": true, \"Widgets\": [ { \"Id\": \"header\", ";
            File.WriteAllText(file, broken);
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = Vm();
                AvaloniaRenderThread.Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
                Assert.False(vm.HudStreamBrowser);
                vm.HudStreamWindow = true;
                vm.SaveHudLayoutNow();
            });
            string[] kept = Directory.GetFiles(settings, "hud.json.unreadable-*");
            Assert.Single(kept);
            Assert.Equal(broken, File.ReadAllText(kept[0]));
            Assert.True(ReadBool(file, "StreamWindow"));
        });
    }
}
