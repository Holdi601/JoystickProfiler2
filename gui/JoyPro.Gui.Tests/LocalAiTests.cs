using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The local AI in one place (Settings > Local AI) with one status line at every AI action, and the 2.2.15 bug: the
// model was on and downloaded, yet "Manage relations with AI" answered "JoyPro has no offline rule for this request;
// switch on the local AI model" because the app had not read the setting yet and sent --llm off.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class LocalAiTests
{
    // The user's request from his log (2.2.15), word for word.
    internal const string UserRequest =
        "Check all my relations that all of them fullfill their functions in what they do properly on all modules in DCS. The current profile hasnt really been much updated since 2023 " +
        "so please go through each relation and see based on the current functionality and expand it on the new since released modules: Mirage F1EE, F-15E Strike Eagle, Mirage F1BE, " +
        "F-4E Phantom II, OH-58D Kiowa Warrior,  CH-47F Chinook, F-5E Remastered, F4U-1D Corsair, MiG-29A Fulcrum, C-130J, La-7, F-100D Super Sabre, F-14B(U)";

    private static MainWindowViewModel CreateVm(LocalAiFakeEngine engine) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "local-ai.pr0file")
        };

    private const string FillPlan =
        "{\"Request\":\"x\",\"Scope\":\"\",\"Method\":\"local AI model (Qwen3 8B (Q4_K_M, non-thinking))\",\"Reply\":\"Checks every relation and fills the new modules.\"," +
        "\"Ops\":[{\"op\":\"check\"}],\"Steps\":[{\"Index\":0,\"Op\":\"check\",\"Text\":\"Check every relation (40): untick 2 aircraft where the action does something else\",\"Ok\":true,\"Error\":\"\"," +
        "\"Before\":\"2 aircraft ticked where the action does something else\",\"After\":\"unticked\",\"Details\":[],\"Warnings\":[]}]," +
        "\"Llm\":{\"Mode\":\"auto\",\"Used\":true,\"Model\":\"qwen3-8b-q4_k_m\"},\"Warnings\":[],\"Errors\":[]}";

    [Fact]
    public async Task ManageWithAi_RightAfterTheStart_AsksTheModelBecauseTheSettingIsOn()
    {
        LocalAiFakeEngine engine = new() { PlanAnswer = FillPlan };
        MainWindowViewModel vm = CreateVm(engine);
        // Like after a restart: nothing about the AI was read yet, the setting is on in the engine.
        Assert.False(vm.LlmEnabled);
        vm.ShowManageWithAiCommand.Execute(null);
        vm.AiPlanText = UserRequest;
        await vm.AskAiPlanCommand.ExecuteAsync(null);

        IReadOnlyList<string> ask = Assert.Single(engine.CallsOf("relations", "plan", "ask"));
        Assert.True(AxisTuningTests.Seq(ask, "--llm", "auto"), "the request went out as " + string.Join(" ", ask));
        // The setting was read before the request.
        int status = engine.Calls.FindIndex(c => AxisTuningTests.Seq(c, "llm", "status"));
        int plan = engine.Calls.FindIndex(c => AxisTuningTests.Seq(c, "relations", "plan", "ask"));
        Assert.InRange(status, 0, plan - 1);
        Assert.DoesNotContain("no offline rule", vm.AiPlanStatus);
        Assert.Contains("The local AI model says: Checks every relation", vm.AiPlanStatus);
        Assert.Single(vm.AiPlanSteps);
        Assert.False(vm.IsLlmStarting);
    }

    [Fact]
    public async Task Status_IsOneLineWithTheSettingAndWhetherTheModelIsLoaded()
    {
        LocalAiFakeEngine engine = new() { Enabled = false };
        MainWindowViewModel vm = CreateVm(engine);
        Assert.Equal("AI: checking...", vm.AiStatusText);
        await vm.EnsureLlmStatusAsync();
        Assert.Equal("AI: off", vm.AiStatusText);
        Assert.True(vm.IsAiSetUp);
        Assert.True(vm.ShowAiTurnOn);
        Assert.False(vm.ShowAiSetUp);
        Assert.False(vm.LocalAiComponentReady);
        Assert.Equal("Set up (Qwen3 8B), switched off.", vm.LocalAiComponentText);

        // One click switches it on (the model that is set up).
        engine.Enabled = true;
        await vm.TurnOnLocalAiCommand.ExecuteAsync(null);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy && vm.LlmEnabled && vm.LlmReady);
        Assert.Contains(engine.Calls, c => AxisTuningTests.Seq(c, "llm", "enable", "qwen3-8b-q4_k_m"));
        Assert.Equal("AI: on (Qwen3 8B) - not loaded right now, starts with your next request", vm.AiStatusText);
        // The same words in the Manage panel's status, the Components card and the chips (one source).
        Assert.Equal(vm.AiStatusText, vm.LlmServerStatusText);
        Assert.True(vm.LocalAiComponentReady);
        Assert.Equal("Set up and on: Qwen3 8B.", vm.LocalAiComponentText);
        Assert.True(vm.IsAiDotReady);
        Assert.True(vm.CanStartLlmNow);
        Assert.Equal("In use: Qwen3 8B, downloaded by JoyPro.", vm.AiModelInUseText);

        // Starting: the status says so while the model loads.
        vm.IsLlmStarting = true;
        Assert.Equal("AI: on (Qwen3 8B) - starting the AI model...", vm.AiStatusText);
        vm.IsLlmStarting = false;
    }

    [Fact]
    public async Task Status_NotSetUp_OffersSetUpWhichOpensSettingsLocalAi()
    {
        LocalAiFakeEngine engine = new() { Enabled = false, Ready = false };
        MainWindowViewModel vm = CreateVm(engine);
        await vm.EnsureLlmStatusAsync();
        Assert.False(vm.IsAiSetUp);
        Assert.True(vm.ShowAiSetUp);
        Assert.False(vm.ShowAiTurnOn);
        Assert.Equal("Not set up (optional).", vm.LocalAiComponentText);

        vm.CurrentStage = UiStage.Relations;
        await vm.OpenLocalAiSettingsCommand.ExecuteAsync(null);
        Assert.Equal(UiStage.Settings, vm.CurrentStage);
        Assert.True(vm.FindSettingsSection(MainWindowViewModel.LocalAiSectionId)!.IsExpanded);

        // On but nothing downloaded: the reason, and Set up.
        engine.Enabled = true;
        await vm.RefreshLlmStatusCommand.ExecuteAsync(null);
        Assert.Equal("AI: on (Qwen3 8B) - not ready: the model is not downloaded yet", vm.AiStatusText);
        Assert.True(vm.ShowAiSetUp);
        Assert.True(vm.IsAiDotProblem);
        Assert.Equal("On, but not ready: the model is not downloaded yet.", vm.LocalAiComponentText);
    }

    [Fact]
    public async Task OwnLlamaCppFiles_TheStatusNamesTheModelInUse()
    {
        LocalAiFakeEngine engine = new() { UsingLocal = true };
        MainWindowViewModel vm = CreateVm(engine);
        await vm.EnsureLlmStatusAsync();
        Assert.True(vm.IsLlmUsingLocal);
        Assert.Equal("AI: on (Qwen3.8-27B-UD-Q3_K_XL) - not loaded right now, starts with your next request", vm.AiStatusText);
        Assert.Contains("Qwen3.8-27B-UD-Q3_K_XL.gguf", vm.AiModelInUseText);
        Assert.Contains(@"C:\llama.cpp\llama-server.exe", vm.AiModelInUseText);
        Assert.False(vm.HasUnusedLocalFiles);
        // Found other files but not applied: the section says they are not in use yet.
        vm.LlmLocalModelPath = @"D:\models\Other.gguf";
        Assert.True(vm.HasUnusedLocalFiles);
        // Switching the AI on again keeps the user's own files (no catalog model id).
        Assert.Equal(["llm", "enable"], vm.BuildLlmEnableArguments(true));
    }

    [Fact]
    public async Task ManageWithAi_AiOff_AsksOfflineAndTheChipOffersToTurnItOn()
    {
        LocalAiFakeEngine engine = new() { Enabled = false };
        MainWindowViewModel vm = CreateVm(engine);
        vm.ShowManageWithAiCommand.Execute(null);
        vm.AiPlanText = UserRequest;
        await vm.AskAiPlanCommand.ExecuteAsync(null);
        Assert.True(AxisTuningTests.Seq(Assert.Single(engine.CallsOf("relations", "plan", "ask")), "--llm", "off"));
        Assert.True(vm.ShowAiTurnOn);
        Assert.True(vm.IsAiKnownOff);
    }

    // ---- with the real engine and the scripted model (engine llm fake-server)

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private static async Task<string> Run(string engine, string data, string arguments)
    {
        ProcessStartInfo start = new(engine, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        start.Environment["JOYPRO_DATA_DIR"] = data;
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, arguments + " failed (" + process.ExitCode + "): " + errors + output);
        return output;
    }

    // A model that does not start says why, with its own last words: a stand-in "llama-server" that ends at once
    // (cmd.exe reads its closed input and ends) instead of a model that never loads.
    [Fact]
    public async Task RealEngine_ModelThatDoesNotStart_SaysWhyInThePanel()
    {
        string? engine = Engine();
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (engine == null || !File.Exists(cmd))
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "local-ai-fail-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(folder);
        string previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        string previousFake = Environment.GetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE") ?? string.Empty;
        // Another test's app may have made this process the owner of a warm model: these runs start and stop their own.
        string previousOwner = Environment.GetEnvironmentVariable("JOYPRO_LLM_OWNER_PID") ?? string.Empty;
        Environment.SetEnvironmentVariable("JOYPRO_LLM_OWNER_PID", null);
        try
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", null);
            string profile = Path.Combine(folder, "user.pr0file");
            await Run(engine, data, "--profile \"" + profile + "\" relations from-text \"gear up and down on all aircraft\"");
            string gguf = Path.Combine(folder, "broken.gguf");
            File.WriteAllBytes(gguf, new byte[64]);
            await Run(engine, data, "llm use-local --server \"" + cmd + "\" --model \"" + gguf + "\"");

            MainWindowViewModel vm = new(new DataDirEngineClient(data), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            vm.ShowManageWithAiCommand.Execute(null);
            vm.AiPlanText = "make a nice layout for my helicopters";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            Assert.True(vm.LlmEnabled);
            Assert.Contains("did not start", vm.AiPlanStatus);
            Assert.Contains("broken", vm.AiPlanStatus); // which model
            Assert.Contains("ended while loading", vm.AiPlanStatus);
            Assert.DoesNotContain("no output", vm.AiPlanStatus); // its last lines are in the reason
            Assert.Empty(vm.AiPlanSteps);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData.Length > 0 ? previousData : null);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", previousFake.Length > 0 ? previousFake : null);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_OWNER_PID", previousOwner.Length > 0 ? previousOwner : null);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The model answers the user's long request with an empty plan (what Qwen3 8B did): JoyPro's own reading fills the
    // named modules and checks every relation, and says why. Then the user's own llama.cpp files ("Use these files"):
    // the next plan names that model.
    [Fact]
    public async Task RealEngine_EmptyModelPlan_FallsBackToFillAndCheck_AndOwnFilesAreTheModelOfTheNextPlan()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "local-ai-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(folder);
        string previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        string previousFake = Environment.GetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE") ?? string.Empty;
        // Another test's app may have made this process the owner of a warm model: these runs start and stop their own.
        string previousOwner = Environment.GetEnvironmentVariable("JOYPRO_LLM_OWNER_PID") ?? string.Empty;
        Environment.SetEnvironmentVariable("JOYPRO_LLM_OWNER_PID", null);
        try
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
            string script = Path.Combine(folder, "empty-plan.txt");
            File.WriteAllText(script, "{\"ops\":[],\"reply\":\"No changes needed as the relations are already properly configured for the specified modules.\"}");
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", script);
            string profile = Path.Combine(folder, "user.pr0file");
            await Run(engine, data, "--profile \"" + profile + "\" relations from-text \"speed brake in and out on all jets\"");
            await Run(engine, data, "--profile \"" + profile + "\" relations from-text \"gear up and down on all aircraft\"");
            // The user's state: the AI switched on in the engine's settings.
            await Run(engine, data, "llm enable qwen3-8b-q4_k_m");

            MainWindowViewModel vm = new(new DataDirEngineClient(data), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            vm.ShowManageWithAiCommand.Execute(null);
            vm.AiPlanText = UserRequest;
            await vm.AskAiPlanCommand.ExecuteAsync(null);

            Assert.True(vm.LlmEnabled);
            Assert.DoesNotContain("no offline rule", vm.AiPlanStatus);
            Assert.Contains("answered with no steps", vm.AiPlanStatus);
            Assert.Contains("The local AI model says: No changes needed", vm.AiPlanStatus);
            Assert.Contains("JoyPro's rules", vm.AiPlanStatus);
            Assert.Contains(vm.AiPlanSteps, s => s.Text.StartsWith("Check", StringComparison.Ordinal) || s.Text.StartsWith("Checked", StringComparison.Ordinal));
            AiPlanStepItem fill = Assert.Single(vm.AiPlanSteps, s => s.Text.StartsWith("Fill", StringComparison.Ordinal));
            Assert.Contains("Mirage-F1EE", fill.Text);
            Assert.Contains("more", fill.Text); // 21 aircraft and seats: the first ones and how many more

            // "Use these files": the user's llama-server.exe and GGUF (stand-ins; the scripted model answers for them).
            string server = Path.Combine(folder, "llama.cpp", "llama-server.exe");
            string gguf = Path.Combine(folder, "models", "Qwen3.8-27B-UD-Q3_K_XL.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(server)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
            File.WriteAllText(server, "stand-in");
            File.WriteAllBytes(gguf, new byte[64]);
            vm.LlmLocalServerPath = server;
            vm.LlmLocalModelPath = gguf;
            Assert.True(vm.HasUnusedLocalFiles);
            await vm.UseLocalLlmCommand.ExecuteAsync(null);
            Assert.True(vm.IsLlmUsingLocal);
            Assert.False(vm.HasUnusedLocalFiles);
            Assert.Contains("Qwen3.8-27B-UD-Q3_K_XL", vm.AiStatusText);
            Assert.Contains("Qwen3.8-27B-UD-Q3_K_XL.gguf", vm.AiModelInUseText);

            vm.AiPlanText = "please make everything right for the Mirage F1EE and the C-130J";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            Assert.Contains("Your model: Qwen3.8-27B-UD-Q3_K_XL.gguf", vm.AiPlanStatus);
            Assert.Contains(vm.AiPlanSteps, s => s.Text.StartsWith("Fill", StringComparison.Ordinal) && s.Text.Contains("Mirage-F1EE") && s.Text.Contains("C-130J"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData.Length > 0 ? previousData : null);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", previousFake.Length > 0 ? previousFake : null);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_OWNER_PID", previousOwner.Length > 0 ? previousOwner : null);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
