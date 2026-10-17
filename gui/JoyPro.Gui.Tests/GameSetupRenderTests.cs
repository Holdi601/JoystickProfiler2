using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders the game setup window (first start) into a PNG without showing it, with a fake engine: found, linked and
// missing games, a portable folder whose marker is checked, a DCS Saved Games variant. It needs the whole application,
// so (like the Axis Tuning page) it renders only when JOYPRO_GAME_SETUP_PREVIEW names an output folder, and then must
// run alone:
//   set JOYPRO_GAME_SETUP_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\game-setup & dotnet test gui/JoyPro.Gui.Tests --filter RenderGameSetupWindow
[SupportedOSPlatform("windows")]
public sealed class GameSetupRenderTests
{
    [Fact]
    public void RenderGameSetupWindow()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GAME_SETUP_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string portable = Path.Combine(outDir, "portable-il2");
        Directory.CreateDirectory(Path.Combine(portable, "bin", "game"));
        File.WriteAllText(Path.Combine(portable, "bin", "game", "Il-2.exe"), string.Empty);

        AvaloniaRenderThread.Run(() =>
        {
            SetupFakeEngine fake = new();
            MainWindowViewModel vm = new(fake, new NoCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            AvaloniaRenderThread.Wait(Done(vm.RefreshGameInstallsCommand.ExecuteAsync(null)));
            vm.BuildGameSetup(firstStart: true);
            vm.GameSetupItems.Single(i => i.GameId == "IL2Game").LinkedFolder = portable;
            vm.GameSetupItems.Single(i => i.GameId == "DCS").ConfigLink = @"C:\Users\Pilot\Saved Games\DCS.openbeta";
            vm.GameSetupItems.Single(i => i.GameId == "X4").LinkedFolder = Path.Combine(outDir, "no-such-folder");
            vm.GameSetupItems.Single(i => i.GameId == "MSFS2020").HasGame = false;

            GameSetupWindow window = new() { DataContext = vm, Width = 1040, Height = 1300 };
            Size size = new(1040, 1300);
            window.Measure(size);
            window.Arrange(new Rect(size));
            Assert.True(window.Content is Control);
            Control content = (Control)window.Content!;
            content.Measure(size);
            content.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
            content.Measure(size);
            content.Arrange(new Rect(size));

            // One "I have this game" box per game (its content is the game's name), ticked for the found and linked
            // ones; "I don't have it" only under the game JoyPro did not find and the user did not tick (Arma 3).
            List<CheckBox> boxes = content.GetVisualDescendantsOfType<CheckBox>().Where(b => b.Content is TextBlock).ToList();
            Assert.Equal(vm.GameSetupItems.Count, boxes.Count);
            Assert.Equal(vm.GameSetupItems.Count(i => i.HasGame), boxes.Count(b => b.IsChecked == true));
            List<CheckBox> hideChoices = content.GetVisualDescendantsOfType<CheckBox>().Where(b => b.Content is string && b.IsVisible).ToList();
            Assert.Single(hideChoices);
            List<string> texts = content.GetVisualDescendantsOfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(texts, t => t.StartsWith("Marker found: bin\\game\\Il-2.exe", StringComparison.Ordinal));
            Assert.Contains(texts, t => t == "The folder does not exist.");
            Assert.Contains(texts, t => t.Contains("DCS.openbeta  (yours)", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.StartsWith("JoyPro found 3 of its 7 games", StringComparison.Ordinal));

            string file = Path.Combine(outDir, "game-setup.png");
            using (RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height)))
            {
                // The window is not shown, so it draws no background: the dark theme's under the content.
                if (content is Panel panel)
                    panel.Background = new SolidColorBrush(Color.Parse("#202020"));
                bitmap.Render(content);
                bitmap.Save(file);
            }
            Assert.True(new FileInfo(file).Length > 1000);

            // Settings > Games after Done: MSFS 2020 is a game the user does not have, listed apart (MSFS 2024 stays).
            fake.MsfsDisabled = true;
            AvaloniaRenderThread.Wait(Done(vm.RefreshGameInstallsCommand.ExecuteAsync(null)));
            Assert.Single(vm.HiddenGameInstalls);
            vm.CurrentStage = UiStage.Settings;
            MainWindow main = new() { DataContext = vm, Width = 1500, Height = 1800 };
            Size pageSize = new(1500, 1800);
            main.Measure(pageSize);
            main.Arrange(new Rect(pageSize));
            Control page = (Control)main.Content!;
            page.Measure(pageSize);
            page.Arrange(new Rect(pageSize));
            AvaloniaRenderThread.Pump();
            page.Measure(pageSize);
            page.Arrange(new Rect(pageSize));
            Assert.Contains(page.GetVisualDescendantsOfType<Button>(), b => (b.Content as string) == "Run setup again");
            if (page is Panel pagePanel)
                pagePanel.Background = new SolidColorBrush(Color.Parse("#202020"));
            string settingsFile = Path.Combine(outDir, "settings-games.png");
            using (RenderTargetBitmap bitmap = new(new PixelSize(1500, 1800)))
            {
                bitmap.Render(page);
                bitmap.Save(settingsFile);
            }
            Assert.True(new FileInfo(settingsFile).Length > 1000);
        });
    }

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private sealed class SetupFakeEngine : IEngineCliClient
    {
        public bool MsfsDisabled { get; set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = arguments.Contains("installs")
                ? "{\"Games\":[" +
                  "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"G:\\\\Program Files\\\\Eagle Dynamics\\\\DCS World\",\"Source\":\"known place\",\"Overridden\":false,\"MarkerFound\":true,\"Markers\":[\"bin\\\\DCS.exe\",\"bin-mt\\\\DCS.exe\"],\"ConfigPaths\":[\"C:\\\\Users\\\\Pilot\\\\Saved Games\\\\DCS\"],\"Note\":\"\"}," +
                  "{\"GameId\":\"MSFS2024\",\"Name\":\"Microsoft Flight Simulator 2024\",\"Installed\":true,\"InstallPath\":\"D:\\\\SteamLibrary\\\\steamapps\\\\common\\\\MSFS2024\",\"Source\":\"steam\",\"Overridden\":false,\"MarkerFound\":true,\"Markers\":[\"FlightSimulator2024.exe\"],\"ConfigPaths\":[],\"Note\":\"\"}," +
                  "{\"GameId\":\"MSFS2020\",\"Name\":\"Microsoft Flight Simulator 2020\",\"Installed\":true,\"InstallPath\":\"D:\\\\SteamLibrary\\\\steamapps\\\\common\\\\MicrosoftFlightSimulator\",\"Source\":\"steam\",\"Overridden\":false,\"MarkerFound\":true,\"Markers\":[\"FlightSimulator.exe\"],\"ConfigPaths\":[],\"Note\":\"\"" +
                  (MsfsDisabled ? ",\"Disabled\":true" : string.Empty) + "}," +
                  "{\"GameId\":\"StarCitizen\",\"Name\":\"Star Citizen\",\"Installed\":true,\"InstallPath\":\"E:\\\\Roberts Space Industries\\\\StarCitizen\",\"Source\":\"override\",\"Overridden\":true,\"MarkerFound\":true,\"Markers\":[\"LIVE\\\\Bin64\\\\StarCitizen.exe\"],\"ConfigPaths\":[\"E:\\\\Roberts Space Industries\\\\StarCitizen\\\\LIVE\\\\USER\\\\Client\\\\0\\\\Profiles\\\\default\"],\"Note\":\"\"}," +
                  "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Installed\":true,\"InstallPath\":\"C:\\\\Users\\\\Pilot\\\\AppData\\\\Local\\\\WarThunder\",\"Source\":\"known place\",\"Overridden\":false,\"MarkerFound\":true,\"Markers\":[\"win64\\\\aces.exe\"],\"ConfigPaths\":[\"C:\\\\Users\\\\Pilot\\\\Documents\\\\My Games\\\\WarThunder\\\\Saves\\\\123\\\\production\\\\machine.blk\"],\"Note\":\"\"}," +
                  "{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"Overridden\":false,\"MarkerFound\":false,\"Markers\":[\"bin\\\\game\\\\Il-2.exe\"],\"ConfigInInstallation\":true,\"ConfigPaths\":[],\"Note\":\"Not found.\"}," +
                  "{\"GameId\":\"X4\",\"Name\":\"X4: Foundations\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"Overridden\":false,\"MarkerFound\":false,\"Markers\":[\"X4.exe\"],\"ConfigPaths\":[],\"Note\":\"Not found.\"}," +
                  "{\"GameId\":\"Arma3\",\"Name\":\"Arma 3\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"Overridden\":false,\"MarkerFound\":false,\"Markers\":[\"arma3_x64.exe\"],\"ConfigPaths\":[],\"Note\":\"Not found.\"}" +
                  "],\"Warnings\":[]}"
                : "{}";
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }
    }

    private sealed class NoCapture : IJoystickCaptureService
    {
        public string LastDiagnostics => string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
