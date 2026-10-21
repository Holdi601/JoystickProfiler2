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

// Relations > Group by description (engine relations from-text) and the optional local AI model (engine llm). The
// engine is faked: the parser itself is tested by engine\tests\smoke\nlgroup.ps1 against the real catalog.
[SupportedOSPlatform("windows")]
public sealed class DescribeGroupTests
{
    [Fact]
    public async Task Preview_ParsesProposalsNodesAndExplanations()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.ToggleDescribeGroupPanelCommand.ExecuteAsync(null);
        Assert.True(vm.IsDescribeGroupPanelOpen);

        vm.DescribeGroupText = "speed brake in and out on all jets";
        fake.Calls.Clear();
        await vm.PreviewDescribeGroupCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "from-text"));
        Assert.Contains("speed brake in and out on all jets", call);
        Assert.Contains("--dry-run", call);
        Assert.True(AxisTuningTests.Seq(call, "--llm", "off"));
        Assert.Contains("--profile", call);

        Assert.Equal(2, vm.DescribeGroupProposals.Count);
        DescribeGroupProposalItem outRelation = vm.DescribeGroupProposals[0];
        Assert.Equal("Speed Brake Out (jets)", outRelation.Name);
        Assert.True(outRelation.IsSelected);
        Assert.Equal(2, outRelation.Nodes.Count);
        DescribeGroupNodeItem a10 = outRelation.Nodes[0];
        Assert.Equal("A-10C, A-10C II", a10.Where);
        Assert.Contains("speed brake switch aft", a10.Explanation);
        Assert.True(a10.IsSelected);
        DescribeGroupNodeItem msfs = outRelation.Nodes[1];
        Assert.Equal("MSFS", msfs.Where);
        Assert.False(msfs.IsSelected); // 0.51: a game that only partly fits starts unticked
        Assert.Contains("Stays in", vm.DescribeGroupProposals[1].Nodes[0].Kept);
        Assert.Contains("Not understood: pls", vm.DescribeGroupStatus);
        Assert.Contains("Aircraft: jets", vm.DescribeGroupStatus);
        Assert.True(vm.HasDescribeGroupProposals);
    }

    [Fact]
    public async Task Create_PassesTickedRelationsAndSkipsUntickedNodes()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.DescribeGroupText = "speed brake in and out on all jets";
        await vm.PreviewDescribeGroupCommand.ExecuteAsync(null);
        vm.DescribeGroupProposals[1].IsSelected = false;
        vm.DescribeGroupProposals[0].Nodes[0].IsSelected = false;

        fake.Calls.Clear();
        await vm.CreateDescribeGroupCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "from-text"));
        Assert.DoesNotContain("--dry-run", call);
        Assert.True(AxisTuningTests.Seq(call, "--select", "Speed Brake Out (jets)"));
        Assert.False(AxisTuningTests.Seq(call, "--select", "Speed Brake In (jets)"));
        Assert.True(AxisTuningTests.Seq(call, "--skip-node", "DCS:d578:A-10C|A-10C II"));
        // MSFS was unticked from the start (low confidence).
        Assert.True(AxisTuningTests.Seq(call, "--skip-node", "MSFS:KEY_SPOILERS_ON:MSFS"));
        Assert.True(AxisTuningTests.Seq(call, "--llm", "off"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "list"));
        Assert.Contains("Created 1", vm.DescribeGroupStatus);
        Assert.Empty(vm.DescribeGroupProposals);
    }

    [Fact]
    public async Task Create_UsesTheModelOnlyWhenThePreviewUsedIt()
    {
        DescribeGroupFakeEngine fake = new() { ModelUsed = true };
        MainWindowViewModel vm = CreateVm(fake);
        vm.DescribeGroupText = "speedy brakes for fast movers";
        await vm.PreviewDescribeGroupCommand.ExecuteAsync(null);
        Assert.Contains("Local model: qwen2.5-1.5b-instruct-q4_k_m", vm.DescribeGroupStatus);
        Assert.Contains("Ignored model answers (not in the lists): F99", vm.DescribeGroupStatus);
        List<string> args = vm.BuildDescribeGroupCreateArguments(vm.DescribeGroupProposals.ToList());
        Assert.True(AxisTuningTests.Seq(args, "--llm", "on"));
    }

    [Fact]
    public async Task Preview_WithoutText_DoesNotCallTheEngine()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        fake.Calls.Clear();
        await vm.PreviewDescribeGroupCommand.ExecuteAsync(null);
        Assert.Empty(fake.Calls);
        Assert.Contains("Describe what to group", vm.DescribeGroupStatus);
    }

    [Fact]
    public async Task LocalModel_StatusEnableConsentDownloadAndRemove()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshLlmStatusCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.LlmModels.Count);
        Assert.Equal("qwen2.5-1.5b-instruct-q4_k_m", vm.SelectedLlmModel?.Id); // the recommendation
        Assert.False(vm.LlmEnabled);
        Assert.Contains("16 GB RAM", vm.LlmHardwareText);
        Assert.Contains("Recommended: Qwen2.5 1.5B Instruct (Q4_K_M) (1.04 GB, runs on the CPU)", vm.LlmRecommendationText);
        Assert.Contains("Apache-2.0", vm.LlmLicenseText);
        Assert.Contains("1.04 GB", vm.LlmLicenseText);
        Assert.Contains("MIT", vm.LlmLicenseText);
        Assert.Contains("Off", vm.LlmStatusText);
        // Loading the status must not switch the model on or off.
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "enable") || AxisTuningTests.Seq(c, "llm", "disable"));

        fake.Calls.Clear();
        vm.LlmEnabled = true;
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "llm", "enable")));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "enable", "qwen2.5-1.5b-instruct-q4_k_m"));

        // No consent, no download.
        fake.Calls.Clear();
        await vm.DownloadLlmModelCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "download"));
        Assert.Contains("I accept", vm.LlmDownloadText);

        vm.LlmLicenseAccepted = true;
        await vm.DownloadLlmModelCommand.ExecuteAsync(null);
        IReadOnlyList<string> download = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "download"));
        Assert.True(AxisTuningTests.Seq(download, "download", "qwen2.5-1.5b-instruct-q4_k_m", "--accept-license"));
        Assert.Equal(100, vm.LlmDownloadPercent);
        Assert.Contains("Downloaded and verified", vm.LlmDownloadText);
        Assert.False(vm.IsLlmDownloading);

        // Choosing another model asks for consent again.
        vm.SelectedLlmModel = vm.LlmModels.Single(m => m.Id == "qwen3-8b-q4_k_m");
        Assert.False(vm.LlmLicenseAccepted);

        fake.Calls.Clear();
        await vm.RemoveLlmModelCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "remove", "qwen3-8b-q4_k_m"));
    }

    [Fact]
    public async Task LocalModel_OneClickDownloadWithChosenRuntime()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshLlmStatusCommand.ExecuteAsync(null);
        Assert.Equal("auto", vm.SelectedLlmRuntime);
        Assert.Contains("about 30 MB", vm.LlmLicenseText);

        // Another runtime build is another download: its size shows and consent is asked again.
        vm.LlmLicenseAccepted = true;
        vm.SelectedLlmRuntime = "cuda";
        Assert.False(vm.LlmLicenseAccepted);
        Assert.Contains("cuda (about 600 MB", vm.LlmLicenseText);
        Assert.Contains("NVIDIA CUDA Toolkit EULA", vm.LlmLicenseText);
        vm.SelectedLlmRuntime = "vulkan";
        Assert.DoesNotContain("NVIDIA", vm.LlmLicenseText);
        vm.SelectedLlmRuntime = "cuda";

        // Consent + one click: download (runtime + model), then the status shows the model on. No separate enable step.
        vm.LlmLicenseAccepted = true;
        fake.Calls.Clear();
        await vm.DownloadLlmModelCommand.ExecuteAsync(null);
        IReadOnlyList<string> download = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "download"));
        Assert.True(AxisTuningTests.Seq(download, "--accept-license", "--runtime", "cuda"));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "enable"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "status"));
        Assert.Contains("The local AI model is on", vm.LlmDownloadText);
    }

    [Fact]
    public async Task LocalModel_FindAndUseOwnLlamaCpp()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshLlmStatusCommand.ExecuteAsync(null);
        Assert.False(vm.IsLlmUsingLocal);

        fake.Calls.Clear();
        await vm.FindLocalLlmCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "find-local"));
        Assert.Equal(@"C:\llama.cpp\llama-server.exe", vm.LlmLocalServerPath);
        Assert.Equal(@"D:\models\Qwen3-8B-Q4_K_M.gguf", vm.LlmLocalModelPath);
        Assert.Contains("fits completely", vm.LlmLocalText);

        fake.Calls.Clear();
        fake.UsingLocal = true;
        await vm.UseLocalLlmCommand.ExecuteAsync(null);
        IReadOnlyList<string> use = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "use-local"));
        Assert.True(AxisTuningTests.Seq(use, "--server", @"C:\llama.cpp\llama-server.exe", "--model", @"D:\models\Qwen3-8B-Q4_K_M.gguf"));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "download"));
        Assert.True(vm.IsLlmUsingLocal);
        Assert.True(vm.LlmEnabled);
        Assert.Contains("your own model Qwen3-8B-Q4_K_M.gguf", vm.LlmStatusText);

        fake.Calls.Clear();
        fake.UsingLocal = false;
        await vm.ClearLocalLlmCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "use-local", "--clear"));
        Assert.False(vm.IsLlmUsingLocal);
        Assert.Equal(string.Empty, vm.LlmLocalModelPath);
    }

    [Fact]
    public async Task LocalModel_UseOwnWithoutPaths_DoesNotCallTheEngine()
    {
        DescribeGroupFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        fake.Calls.Clear();
        await vm.UseLocalLlmCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "llm", "use-local"));
        Assert.Contains("Choose your llama-server.exe", vm.LlmLocalText);
    }

    [Fact]
    public async Task LocalModel_ProgressFileDrivesTheProgressBar()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "llm-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string progress = Path.Combine(folder, "download-progress.json");
        File.WriteAllText(progress, "{\"State\":\"downloading\",\"Percent\":42.5,\"Message\":\"model.gguf: 470 MB of 1.04 GB\"}");
        DescribeGroupFakeEngine fake = new() { ProgressFile = progress };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshLlmStatusCommand.ExecuteAsync(null);
        vm.ReadLlmProgress();
        Assert.Equal(42.5, vm.LlmDownloadPercent);
        Assert.Equal("model.gguf: 470 MB of 1.04 GB", vm.LlmDownloadText);
        Directory.Delete(folder, true);
    }

    // Renders the panel with a preview and the local model section into a PNG (like GuiParityRenderTests: only when
    // JOYPRO_GUI_PARITY_PREVIEW names an output folder, and then alone:
    //   set JOYPRO_GUI_PARITY_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\gui-parity & dotnet test gui/JoyPro.Gui.Tests --filter RenderDescribeGroupPanel
    [Fact]
    public void RenderDescribeGroupPanel()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            DescribeGroupFakeEngine fake = new();
            MainWindowViewModel vm = CreateVm(fake);
            vm.CurrentStage = UiStage.Relations;
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.ToggleDescribeGroupPanelCommand.ExecuteAsync(null)));
            vm.DescribeGroupText = "speed brake in and out on all jets";
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.PreviewDescribeGroupCommand.ExecuteAsync(null)));
            // The setup lives in Settings > Local AI; the tab shows the one status line with its link.
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "relations-describe-group.png"), "Auto-build relations from all my actions", 1300, page =>
            {
                List<string> texts = page.GetVisualDescendantsOfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains("AI: off", texts);
                Assert.DoesNotContain(page.GetVisualDescendantsOfType<Avalonia.Controls.Expander>(), e => e.IsEffectivelyVisible && (e.Header as string) == "Local AI model (optional)");
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Settings");
            });
            // Open the first relation.
            vm.DescribeGroupProposals[0].IsExpanded = true;
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "relations-describe-group-open.png"), "Auto-build relations from all my actions", 1900, page =>
            {
                List<string> texts = page.GetVisualDescendantsOfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, t => t.Contains("speed brake switch aft"));
            });
            // Settings > Local AI: everything of the setup in one place.
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.OpenLocalAiSettingsCommand.ExecuteAsync(null)));
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "settings-local-ai.png"), "Local AI", 1500, page =>
            {
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.CheckBox>(), c => c.IsEffectivelyVisible && (c.Content as string) == "Use the local AI model");
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Download and switch on");
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Find on this PC");
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Start now");
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Stop now");
            });
        });
    }

    private static MainWindowViewModel CreateVm(DescribeGroupFakeEngine fake)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        vm.ProfilePath = Path.Combine(tempRoot, "describe-group.pr0file");
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DescribeGroupFakeEngine : IEngineCliClient
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public bool ModelUsed { get; set; }

    public string ProgressFile { get; set; } = string.Empty;

    public bool UsingLocal { get; set; }

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = BuildOutput(arguments),
            StandardError = string.Empty,
            Arguments = arguments.ToArray()
        });
    }

    private string BuildOutput(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "from-text"))
        {
            bool dryRun = args.Contains("--dry-run");
            string llm = ModelUsed
                ? "{\"Mode\":\"auto\",\"Used\":true,\"Model\":\"qwen2.5-1.5b-instruct-q4_k_m\",\"ProcessId\":4242,\"ProcessExited\":true,\"DurationMs\":900,\"Answer\":\"{}\",\"Note\":\"speed brake on jets\",\"Dropped\":[\"F99\"]}"
                : "{\"Mode\":\"off\",\"Used\":false,\"Model\":\"\",\"ProcessId\":0,\"ProcessExited\":false,\"DurationMs\":0,\"Answer\":\"\",\"Note\":\"\",\"Dropped\":[]}";
            return "{\"Request\":\"speed brake in and out on all jets\",\"Method\":\"offline rules\",\"DryRun\":" + (dryRun ? "true" : "false") + "," +
                   "\"Understood\":{\"Functions\":[\"Speed Brake Out [airbakeextend] from 'speed brake' + 'out'\",\"Speed Brake In [airbrakeretract] from 'speed brake' + 'in'\"],\"Aircraft\":\"jets\",\"AircraftCount\":61}," +
                   "\"NotUnderstood\":[\"pls\"],\"Llm\":" + llm + "," +
                   "\"RelationsCreated\":" + (dryRun ? "2" : "1") + ",\"RelationsExtended\":0,\"NodesAdded\":" + (dryRun ? "5" : "1") + "," +
                   "\"Proposals\":[" +
                   "{\"Name\":\"Speed Brake Out (jets)\",\"Function\":\"Speed Brake Out\",\"Slots\":[\"airbakeextend\"],\"IsAxis\":false,\"Existing\":false,\"Selected\":true,\"NodesAdded\":3,\"Why\":\"'speed brake' + 'out'\"," +
                   "\"Nodes\":[{\"Spec\":\"DCS:d578:A-10C|A-10C II\",\"Game\":\"DCS\",\"Id\":\"d578\",\"Planes\":[\"A-10C\",\"A-10C II\"],\"Confidence\":0.95,\"Explanations\":[\"A-10C: throttle: speed brake switch aft (catalog confirmed); jets\",\"A-10C II: throttle: speed brake switch aft (catalog confirmed); jets\"],\"KeptIn\":{}}," +
                   "{\"Spec\":\"MSFS:KEY_SPOILERS_ON:MSFS\",\"Game\":\"MSFS\",\"Id\":\"KEY_SPOILERS_ON\",\"Planes\":[\"MSFS\"],\"Confidence\":0.51,\"Explanations\":[\"MSFS: throttle: speed brake/spoilers out (catalog new); MSFS binds are shared by all its aircraft; it also has jets\"],\"KeptIn\":{}}]," +
                   "\"Missing\":[\"P-51D\"]}," +
                   "{\"Name\":\"Speed Brake In (jets)\",\"Function\":\"Speed Brake In\",\"Slots\":[\"airbrakeretract\"],\"IsAxis\":false,\"Existing\":false,\"Selected\":true,\"NodesAdded\":2,\"Why\":\"'speed brake' + 'in'\"," +
                   "\"Nodes\":[{\"Spec\":\"DCS:d579:A-10C\",\"Game\":\"DCS\",\"Id\":\"d579\",\"Planes\":[\"A-10C\"],\"Confidence\":0.95,\"Explanations\":[\"A-10C: throttle: speed brake switch forward (catalog confirmed); jets\"],\"KeptIn\":{\"A-10C\":\"My Airbrake\"}}]," +
                   "\"Missing\":[]}]," +
                   "\"Kept\":[\"A-10C d579 stays in 'My Airbrake' (wanted: 'Speed Brake In (jets)')\"],\"Warnings\":[],\"Errors\":[]}";
        }
        if (AxisTuningTests.Seq(args, "llm", "status"))
        {
            string models = string.Join(",", new[]
            {
                Model("qwen2.5-0.5b-instruct-q4_k_m", "Qwen2.5 0.5B Instruct (Q4_K_M)", "tiny", "469 MB", false),
                Model("qwen2.5-1.5b-instruct-q4_k_m", "Qwen2.5 1.5B Instruct (Q4_K_M)", "small", "1.04 GB", true),
                Model("qwen3-4b-q4_k_m", "Qwen3 4B (Q4_K_M, non-thinking)", "medium", "2.33 GB", false),
                Model("qwen3-8b-q4_k_m", "Qwen3 8B (Q4_K_M, non-thinking)", "large", "4.68 GB", false)
            });
            string local = UsingLocal
                ? "\"UsingLocal\":true,\"LocalReady\":true,\"LocalServer\":\"C:\\\\llama.cpp\\\\llama-server.exe\",\"LocalModel\":\"D:\\\\models\\\\Qwen3-8B-Q4_K_M.gguf\",\"LocalGpuLayers\":\"\","
                : "\"UsingLocal\":false,\"LocalReady\":false,\"LocalServer\":\"\",\"LocalModel\":\"\",\"LocalGpuLayers\":\"\",";
            return "{\"Enabled\":" + (UsingLocal ? "true" : "false") + ",\"Ready\":" + (UsingLocal ? "true" : "false") + ",\"SelectedModel\":\"" + (UsingLocal ? "local" : "qwen2.5-1.5b-instruct-q4_k_m") + "\"," + local +
                   "\"RuntimeChoice\":\"auto\",\"RuntimeSizesMb\":{\"cpu\":30,\"vulkan\":60,\"cuda\":600},\"RuntimeExtraLicenses\":{\"cuda\":\"NVIDIA CUDA Toolkit EULA\"}," +
                   "\"Hardware\":{\"Source\":\"detected\",\"RamBytes\":17179869184,\"RamAvailableBytes\":8589934592,\"CpuName\":\"Test CPU\",\"LogicalCores\":8,\"PhysicalCores\":4,\"Avx\":true,\"Avx2\":true,\"Avx512\":false,\"Gpus\":[]}," +
                   "\"Recommendation\":{\"ModelId\":\"qwen2.5-1.5b-instruct-q4_k_m\",\"Tier\":\"small\",\"Runtime\":\"cpu\",\"GpuLayers\":0,\"Threads\":4,\"Gpu\":\"\",\"Reason\":\"no dedicated GPU, 16 GB RAM with AVX2: the 1.5B model on the CPU.\"}," +
                   "\"Models\":[" + models + "],\"RuntimeName\":\"llama.cpp (llama-server)\",\"RuntimeInstalled\":false,\"RuntimeFlavor\":\"cpu\",\"RuntimeVersion\":\"\",\"RuntimeApproxMb\":30," +
                   "\"RuntimeLicense\":\"MIT\",\"RuntimeLicenseUrl\":\"https://github.com/ggml-org/llama.cpp/blob/master/LICENSE\",\"RuntimeHomepage\":\"https://github.com/ggml-org/llama.cpp\"," +
                   "\"DataFolder\":\"C:\\\\Temp\\\\Llm\",\"ProgressFile\":" + System.Text.Json.JsonSerializer.Serialize(ProgressFile) + ",\"CancelFile\":\"\",\"Catalog\":\"models.json\"}";
        }
        if (AxisTuningTests.Seq(args, "llm", "download"))
            return "Model: qwen2.5-1.5b-instruct-q4_k_m\nModel qwen2.5-1.5b-instruct-q4_k_m downloaded and verified.";
        if (AxisTuningTests.Seq(args, "llm", "find-local"))
            return "{\"Servers\":[{\"Path\":\"C:\\\\llama.cpp\\\\llama-server.exe\",\"Backend\":\"cuda\",\"Recommended\":true}]," +
                   "\"Models\":[{\"Path\":\"D:\\\\models\\\\Qwen3-8B-Q4_K_M.gguf\",\"Name\":\"Qwen3-8B-Q4_K_M.gguf\",\"SizeBytes\":5027783488,\"Size\":\"4.68 GB\",\"FitsGpu\":true,\"FitsRam\":true,\"Recommended\":true}]," +
                   "\"BestServer\":\"C:\\\\llama.cpp\\\\llama-server.exe\",\"BestModel\":\"D:\\\\models\\\\Qwen3-8B-Q4_K_M.gguf\"," +
                   "\"Reason\":\"Qwen3-8B-Q4_K_M.gguf (4.68 GB) fits completely on the Test GPU (12 GB).\",\"Searched\":[\"C:\\\\llama.cpp\"],\"Seconds\":0.1}";
        if (AxisTuningTests.Seq(args, "llm", "use-local", "--clear"))
            return "JoyPro no longer uses your own llama.cpp / GGUF; the local AI model is off.";
        if (AxisTuningTests.Seq(args, "llm", "use-local"))
            return "Using your llama.cpp (C:\\llama.cpp\\llama-server.exe) with Qwen3-8B-Q4_K_M.gguf (4.68 GB); the local AI model is on. Nothing was downloaded.";
        if (AxisTuningTests.Seq(args, "relations", "list"))
            return "{\"Relations\":[]}";
        if (AxisTuningTests.Seq(args, "binds", "list"))
            return "{\"Binds\":[]}";
        return "ok";
    }

    private static string Model(string id, string label, string tier, string size, bool recommended) =>
        "{\"Id\":\"" + id + "\",\"Label\":\"" + label + "\",\"Tier\":\"" + tier + "\",\"Parameters\":\"\",\"Quantization\":\"Q4_K_M\",\"SizeBytes\":1,\"Size\":\"" + size + "\"," +
        "\"License\":\"Apache-2.0\",\"LicenseUrl\":\"https://huggingface.co/x/LICENSE\",\"Source\":\"https://huggingface.co/x\",\"Installed\":false,\"Recommended\":" + (recommended ? "true" : "false") + ",\"Path\":\"\"}";
}
