using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The AI assist offscreen (opt-in, needs the built engine): Manage with AI with a plan, Edit with AI, and the local AI
// model's status with a running stand-in server.
//   set JOYPRO_AI_ASSIST_OUT=<folder> & dotnet test gui/JoyPro.Gui.Tests --filter AiAssistRenderTests
[SupportedOSPlatform("windows")]
public sealed class AiAssistRenderTests
{
    [Fact]
    public void RenderAiAssist()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_AI_ASSIST_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        string folder = Path.Combine(outDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(folder, "data"));
        Process server = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            string profile = Path.Combine(folder, "plan.pr0file");
            using (Process start = Process.Start(new ProcessStartInfo(engine, "--profile \"" + profile + "\" relations from-text \"speed brake in and out on all jets\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            {
                start.StandardOutput.ReadToEnd();
                start.WaitForExit();
            }
            string state = Path.Combine(folder, "server.json");
            File.WriteAllText(state, "{\"Pid\":" + server.Id.ToString(CultureInfo.InvariantCulture) + ",\"Model\":\"Qwen3 8B (Q4_K_M, non-thinking)\",\"StartedUtc\":\"" +
                server.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\",\"LastUsedUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",\"BusyPid\":0}");

            AvaloniaRenderThread.Run(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                MainWindowViewModel vm = new(new DataDirEngineClient(Path.Combine(folder, "data")), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                vm.UseLlmServerMonitor(new LlmServerMonitor(state) { VramQuery = _ => 5L * 1024 * 1024 * 1024 + 300L * 1024 * 1024 });
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                vm.CurrentStage = UiStage.Relations;
                vm.SelectedRelation = vm.Relations.First();
                AvaloniaRenderThread.Wait(vm.PollLlmServerAsync());
                vm.LlmEnabled = true;
                AvaloniaRenderThread.Pump();

                vm.ShowManageWithAiCommand.Execute(null);
                vm.AiPlanText = "append _sc to every relation that contains Speed Brake";
                AvaloniaRenderThread.Wait(vm.AskAiPlanCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "manage-with-ai-" + w + ".png"));

                vm.AiPlanText = "fill the F-15E Strike Eagle and the F4U-1D Corsair into my relations where they fit";
                AvaloniaRenderThread.Wait(vm.AskAiPlanCommand.ExecuteAsync(null));
                if (vm.AiFillRelations.Count > 0)
                    vm.AiFillRelations[0].IsExpanded = true;
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "fill-with-ai-" + w + ".png"));

                // Every game: a relation for another game's function, and a question about a game's action.
                vm.AiPlanText = "make a relation for landing lights in X-Plane 12";
                AvaloniaRenderThread.Wait(vm.AskAiPlanCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "manage-games-" + w + ".png"));
                vm.AiPlanText = "what does Landing gear down do in X-Plane 12";
                AvaloniaRenderThread.Wait(vm.AskAiPlanCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Pump();
                UiDesignRenderTests.RenderWindow(vm, 2267, 1250, Path.Combine(outDir, "manage-question-2267.png"));
                vm.DiscardAiPlanCommand.Execute(null);
                vm.CloseAiPlanCommand.Execute(null);

                // The JoyPro assistant (local AI off: its offline brain) on Star Citizen and Elite Dangerous.
                vm.LlmEnabled = false;
                vm.OpenAssistantCommand.Execute(null);
                vm.AssistantInput = "put the landing gear of sc into Landing Gear";
                AvaloniaRenderThread.Wait(vm.AskAssistantAsync());
                vm.AssistantInput = "what is boost in Elite Dangerous";
                AvaloniaRenderThread.Wait(vm.AskAssistantAsync());
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "assistant-games-" + w + ".png"));
                vm.LlmEnabled = true;

                // While a request runs (the server's BusyPid is a living process) the chip says so and counts nothing down.
                File.WriteAllText(state, "{\"Pid\":" + server.Id.ToString(CultureInfo.InvariantCulture) + ",\"Model\":\"Qwen3 8B (Q4_K_M, non-thinking)\",\"StartedUtc\":\"" +
                    server.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\",\"LastUsedUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"BusyPid\":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "}");
                AvaloniaRenderThread.Wait(vm.PollLlmServerAsync());
                AvaloniaRenderThread.Pump();
                Assert.Contains("answering", vm.LlmServerChipText);
                Assert.DoesNotContain("stops in", vm.LlmServerChipText);
                UiDesignRenderTests.RenderWindow(vm, 1280, 800, Path.Combine(outDir, "assistant-busy-1280.png"));
                File.WriteAllText(state, "{\"Pid\":" + server.Id.ToString(CultureInfo.InvariantCulture) + ",\"Model\":\"Qwen3 8B (Q4_K_M, non-thinking)\",\"StartedUtc\":\"" +
                    server.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\",\"LastUsedUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",\"BusyPid\":0}");
                AvaloniaRenderThread.Wait(vm.PollLlmServerAsync());
                AvaloniaRenderThread.Pump();
                // Idle again: the default idle time of 30 minutes counts down from the last request.
                Assert.Matches(@"stops in (29|30):", vm.LlmServerChipText);

                vm.ShowEditWithAiCommand.Execute(null);
                vm.AiPlanText = "rename it to Brakes in";
                AvaloniaRenderThread.Wait(vm.AskAiPlanCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Pump();
                UiDesignRenderTests.RenderWindow(vm, 2267, 1250, Path.Combine(outDir, "edit-with-ai-2267.png"));

                vm.ShowOrganizeByDescriptionCommand.Execute(null);
                AvaloniaRenderThread.Wait(vm.PollLlmServerAsync());
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "describe-ai-chip-" + w + ".png"));

                // Settings > Local AI (the whole setup) and Settings > Components (its card says the same).
                vm.CollapseAllSettingsCommand.Execute(null);
                AvaloniaRenderThread.Wait(vm.OpenLocalAiSettingsCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.PollLlmServerAsync());
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "settings-local-ai-" + w + ".png"));
                vm.CollapseAllSettingsCommand.Execute(null);
                vm.OpenSettingsSectionCommand.Execute("components");
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "settings-components-" + w + ".png"));
                vm.LlmEnabled = false;
            });
        }
        finally
        {
            try { server.Kill(); } catch (InvalidOperationException) { }
            server.Dispose();
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous.Length > 0 ? previous : null);
        }
    }
}
