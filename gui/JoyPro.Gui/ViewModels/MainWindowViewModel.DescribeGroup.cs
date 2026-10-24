using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// "Group by description": the user writes what belongs together ("speed brake in and out on all jets"); the engine
// proposes relations with an explanation per aircraft (engine relations from-text). The optional local AI model
// (engine llm ...) is off until the user ticks it, and downloaded only after the user accepted size and license.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<DescribeGroupProposalItem> DescribeGroupProposals { get; } = [];

    public ObservableCollection<LlmModelItem> LlmModels { get; } = [];

    private bool describeGroupUsedModel;
    private bool loadingLlmStatus;
    private string llmProgressFile = string.Empty;
    private string llmCancelFile = string.Empty;

    [ObservableProperty]
    private bool isDescribeGroupPanelOpen;

    [ObservableProperty]
    private string describeGroupText = string.Empty;

    [ObservableProperty]
    private string describeGroupStatus = string.Empty;

    [ObservableProperty]
    private bool llmEnabled;

    [ObservableProperty]
    private LlmModelItem? selectedLlmModel;

    [ObservableProperty]
    private string llmHardwareText = string.Empty;

    [ObservableProperty]
    private string llmRecommendationText = string.Empty;

    [ObservableProperty]
    private string llmStatusText = string.Empty;

    [ObservableProperty]
    private bool llmLicenseAccepted;

    [ObservableProperty]
    private bool isLlmDownloading;

    [ObservableProperty]
    private double llmDownloadPercent;

    [ObservableProperty]
    private string llmDownloadText = string.Empty;

    [ObservableProperty]
    private string llmRuntimeText = string.Empty;

    // The llama.cpp build to download: auto (Vulkan with a graphics card, else CPU), vulkan, cuda (NVIDIA) or cpu.
    public IReadOnlyList<string> LlmRuntimeChoices { get; } = ["auto", "vulkan", "cuda", "cpu"];

    [ObservableProperty]
    private string selectedLlmRuntime = "auto";

    // A llama.cpp and GGUF the user already has (engine llm find-local / use-local): no download.
    [ObservableProperty]
    private string llmLocalServerPath = string.Empty;

    [ObservableProperty]
    private string llmLocalModelPath = string.Empty;

    [ObservableProperty]
    private string llmLocalText = string.Empty;

    [ObservableProperty]
    private bool isLlmUsingLocal;

    private readonly Dictionary<string, int> llmRuntimeSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> llmRuntimeExtraLicenses = new(StringComparer.OrdinalIgnoreCase);
    private string llmRuntimeName = string.Empty;
    private string llmRuntimeLicense = string.Empty;
    private bool llmRuntimeInstalled;
    private string llmRuntimeFlavor = string.Empty;

    public bool HasDescribeGroupProposals => DescribeGroupProposals.Count > 0;

    public string LlmLicenseText
    {
        get
        {
            LlmModelItem? model = SelectedLlmModel;
            if (model == null)
                return string.Empty;
            return T("Downloads {0} ({1}) from {2}, license {3} ({4}){5}. Everything runs on this PC; nothing is sent anywhere.", model.Label, model.Size, model.Source, model.License, model.LicenseUrl, (LlmRuntimeText.Length > 0 ? T(", and {0}", LlmRuntimeText) : string.Empty));
        }
    }

    partial void OnSelectedLlmModelChanged(LlmModelItem? value)
    {
        OnPropertyChanged(nameof(LlmLicenseText));
        // Consent belongs to one model: choosing another one asks again (a status refresh keeps it).
        if (!loadingLlmStatus)
            LlmLicenseAccepted = false;
    }

    partial void OnLlmRuntimeTextChanged(string value) => OnPropertyChanged(nameof(LlmLicenseText));

    // Another runtime build is another download: size and license text follow, consent is asked again.
    partial void OnSelectedLlmRuntimeChanged(string value)
    {
        UpdateLlmRuntimeText();
        if (!loadingLlmStatus)
            LlmLicenseAccepted = false;
    }

    private void UpdateLlmRuntimeText()
    {
        string flavor = SelectedLlmRuntime is { Length: > 0 } chosen && chosen != "auto" ? chosen : llmRuntimeFlavor;
        bool needed = !llmRuntimeInstalled || !string.Equals(flavor, llmRuntimeFlavor, StringComparison.OrdinalIgnoreCase);
        llmRuntimeSizes.TryGetValue(flavor, out int mb);
        // A flavor with a second archive of another maker (cuda: NVIDIA's CUDA runtime DLLs) names that license too.
        string extraLicense = llmRuntimeExtraLicenses.TryGetValue(flavor, out string? extra) && extra.Length > 0 ? T(", plus {0}", extra) : string.Empty;
        LlmRuntimeText = needed && llmRuntimeName.Length > 0
            ? T("{0} {1} (about {2} MB, {3}{4})", llmRuntimeName, flavor, mb.ToString(CultureInfo.InvariantCulture), llmRuntimeLicense, extraLicense)
            : string.Empty;
    }

    partial void OnLlmEnabledChanged(bool value)
    {
        OnLocalAiStatusChanged();
        if (loadingLlmStatus)
            return;
        _ = SetLlmEnabledAsync(value);
    }

    [RelayCommand]
    private async Task ToggleDescribeGroupPanelAsync()
    {
        IsDescribeGroupPanelOpen = !IsDescribeGroupPanelOpen;
        if (IsDescribeGroupPanelOpen)
            await EnsureLlmStatusAsync();
    }

    [RelayCommand]
    private async Task PreviewDescribeGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(DescribeGroupText))
        {
            DescribeGroupStatus = T("Describe what to group, e.g. \"speed brake in and out on all jets\".");
            return;
        }
        await EnsureLlmStatusAsync();
        List<string> args = ["relations", "from-text", DescribeGroupText.Trim(), "--dry-run", "--llm", LlmEnabled ? "auto" : "off"];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Reading your description..."), forceText: false);
        DescribeGroupProposals.Clear();
        OnPropertyChanged(nameof(HasDescribeGroupProposals));
        if (result == null)
            return;
        ParseDescribeGroupResult(result);
    }

    private void ParseDescribeGroupResult(EngineCommandResult result)
    {
        describeGroupUsedModel = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            List<string> lines = [];
            JsonElement understood = root.TryGetProperty("Understood", out JsonElement u) ? u : default;
            if (understood.ValueKind == JsonValueKind.Object)
            {
                List<string> functions = ReadStringArray(understood, "Functions");
                lines.Add(T("Functions: {0}", (functions.Count == 0 ? "-" : string.Join("; ", functions))));
                lines.Add(T("Aircraft: {0}", ReadJsonString(understood, "Aircraft")));
            }
            lines.Add(T("How: {0}", ReadJsonString(root, "Method")));
            List<string> notUnderstood = ReadStringArray(root, "NotUnderstood");
            if (notUnderstood.Count > 0)
                lines.Add(T("Not understood: {0}", string.Join(", ", notUnderstood)));
            if (root.TryGetProperty("Llm", out JsonElement llm) && llm.ValueKind == JsonValueKind.Object)
            {
                describeGroupUsedModel = ReadJsonBool(llm, "Used");
                string note = ReadJsonString(llm, "Note");
                if (describeGroupUsedModel || note.Length > 0)
                    lines.Add(T("Local model: {0}{1}", (describeGroupUsedModel ? ReadJsonString(llm, "Model") : "not used"), (note.Length > 0 ? " (" + note + ")" : string.Empty)));
                List<string> dropped = ReadStringArray(llm, "Dropped");
                if (dropped.Count > 0)
                    lines.Add(T("Ignored model answers (not in the lists): {0}", string.Join(", ", dropped)));
            }
            foreach (string warning in ReadStringArray(root, "Warnings").Take(5))
                lines.Add(T("Note: {0}", warning));
            foreach (string error in ReadStringArray(root, "Errors"))
                lines.Add(T("Error: {0}", error));

            if (root.TryGetProperty("Proposals", out JsonElement proposals) && proposals.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement proposal in proposals.EnumerateArray())
                {
                    List<DescribeGroupNodeItem> nodes = [];
                    if (proposal.TryGetProperty("Nodes", out JsonElement nodeList) && nodeList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement node in nodeList.EnumerateArray())
                        {
                            double confidence = node.TryGetProperty("Confidence", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
                            List<string> kept = [];
                            if (node.TryGetProperty("KeptIn", out JsonElement keptIn) && keptIn.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty plane in keptIn.EnumerateObject())
                                    kept.Add(T("{0} in {1}", plane.Name, plane.Value.GetString()));
                            }
                            string nodeGame = ReadJsonString(node, "Game");
                            string nodeId = ReadJsonString(node, "Id");
                            List<string> nodePlanes = ReadStringArray(node, "Planes");
                            nodes.Add(new DescribeGroupNodeItem(
                                ReadJsonString(node, "Spec"), nodeGame, nodeId, nodePlanes,
                                confidence, ReadStringArray(node, "Explanations"), kept)
                            {
                                Title = ResolveNodeTitle(nodeGame, nodePlanes.FirstOrDefault() ?? "Default", nodeId)
                            });
                        }
                    }
                    DescribeGroupProposals.Add(new DescribeGroupProposalItem(
                        ReadJsonString(proposal, "Name"), ReadJsonString(proposal, "Function"), ReadJsonBool(proposal, "IsAxis"), ReadJsonBool(proposal, "Existing"),
                        ReadJsonString(proposal, "Why"), nodes, ReadStringArray(proposal, "Missing")));
                }
            }
            DescribeGroupStatus = string.Join(Environment.NewLine, lines);
        }
        catch (JsonException)
        {
            DescribeGroupStatus = (result.StandardError + result.StandardOutput).Trim();
        }
        if (result.ExitCode != 0 && DescribeGroupStatus.Length == 0)
            DescribeGroupStatus = result.StandardError.Trim();
        OnPropertyChanged(nameof(HasDescribeGroupProposals));
    }

    [RelayCommand]
    private async Task CreateDescribeGroupAsync()
    {
        List<DescribeGroupProposalItem> chosen = DescribeGroupProposals.Where(p => p.IsSelected).ToList();
        if (string.IsNullOrWhiteSpace(DescribeGroupText) || chosen.Count == 0)
        {
            DescribeGroupStatus = T("Preview first, then tick the relations to create.");
            return;
        }
        List<string> args = BuildDescribeGroupCreateArguments(chosen);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Creating relations from your description..."), forceText: false);
        if (result == null)
            return;
        string text = result.StandardOutput;
        DescribeGroupProposals.Clear();
        ParseDescribeGroupResult(result);
        if (result.ExitCode == 0)
        {
            int created = 0, extended = 0, nodes = 0;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(text));
                created = doc.RootElement.TryGetProperty("RelationsCreated", out JsonElement c) ? c.GetInt32() : 0;
                extended = doc.RootElement.TryGetProperty("RelationsExtended", out JsonElement e) ? e.GetInt32() : 0;
                nodes = doc.RootElement.TryGetProperty("NodesAdded", out JsonElement n) ? n.GetInt32() : 0;
            }
            catch (JsonException)
            {
            }
            DescribeGroupStatus = T("Created {0} and extended {1} relations with {2} commands.{3}{4}", created.ToString(CultureInfo.InvariantCulture), extended.ToString(CultureInfo.InvariantCulture), nodes.ToString(CultureInfo.InvariantCulture), Environment.NewLine, DescribeGroupStatus);
            DescribeGroupProposals.Clear();
            OnPropertyChanged(nameof(HasDescribeGroupProposals));
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
        }
    }

    // The same answer as the preview: the model only when the preview used it; ticked relations; unticked nodes skipped.
    internal List<string> BuildDescribeGroupCreateArguments(IReadOnlyList<DescribeGroupProposalItem> chosen)
    {
        List<string> args = ["relations", "from-text", DescribeGroupText.Trim(), "--llm", describeGroupUsedModel ? "on" : "off"];
        foreach (DescribeGroupProposalItem proposal in chosen)
        {
            args.Add("--select");
            args.Add(proposal.Name);
            foreach (DescribeGroupNodeItem node in proposal.Nodes.Where(n => !n.IsSelected && n.Spec.Length > 0))
            {
                args.Add("--skip-node");
                args.Add(node.Spec);
            }
        }
        return args;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Local AI model
    // ---------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshLlmStatusAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["llm", "status"], includeProfile: false, mutatesProfile: false, busyLabel: N("Checking the local AI model..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        ApplyLlmStatus(result.StandardOutput);
    }

    internal void ApplyLlmStatus(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            loadingLlmStatus = true;
            string selected = ReadJsonString(root, "SelectedModel");
            string keep = SelectedLlmModel?.Id ?? selected;
            LlmModels.Clear();
            if (root.TryGetProperty("Models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement model in models.EnumerateArray())
                {
                    LlmModels.Add(new LlmModelItem(
                        ReadJsonString(model, "Id"), ReadJsonString(model, "Label"), ReadJsonString(model, "Tier"), ReadJsonString(model, "Size"),
                        ReadJsonString(model, "License"), ReadJsonString(model, "LicenseUrl"), ReadJsonString(model, "Source"),
                        ReadJsonBool(model, "Installed"), ReadJsonBool(model, "Recommended")));
                }
            }
            SelectedLlmModel = LlmModels.FirstOrDefault(m => m.Id == keep) ?? LlmModels.FirstOrDefault(m => m.Recommended) ?? LlmModels.FirstOrDefault();
            LlmEnabled = ReadJsonBool(root, "Enabled");
            llmProgressFile = ReadJsonString(root, "ProgressFile");
            llmCancelFile = ReadJsonString(root, "CancelFile");

            if (root.TryGetProperty("Hardware", out JsonElement hw) && hw.ValueKind == JsonValueKind.Object)
            {
                double ram = hw.TryGetProperty("RamBytes", out JsonElement r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() / (1024.0 * 1024 * 1024) : 0;
                int cores = hw.TryGetProperty("PhysicalCores", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                List<string> gpus = [];
                if (hw.TryGetProperty("Gpus", out JsonElement gpuList) && gpuList.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement gpu in gpuList.EnumerateArray())
                    {
                        double vram = gpu.TryGetProperty("VramBytes", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / (1024.0 * 1024 * 1024) : 0;
                        gpus.Add(T("{0} ({1} GB)", ReadJsonString(gpu, "Name"), vram.ToString("0.#", CultureInfo.InvariantCulture)));
                    }
                }
                LlmHardwareText = T("This PC: {0} GB RAM, {1} CPU cores{2}, {3}", ram.ToString("0.#", CultureInfo.InvariantCulture), cores.ToString(CultureInfo.InvariantCulture), (ReadJsonBool(hw, "Avx2") ? " (AVX2)" : T(" (no AVX2)")), (gpus.Count == 0 ? T("no graphics card found") : string.Join(", ", gpus)));
            }
            if (root.TryGetProperty("Recommendation", out JsonElement rec) && rec.ValueKind == JsonValueKind.Object)
            {
                string id = ReadJsonString(rec, "ModelId");
                LlmModelItem? model = LlmModels.FirstOrDefault(m => m.Id == id);
                bool gpu = rec.TryGetProperty("GpuLayers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Number && layers.GetInt32() > 0;
                LlmRecommendationText = model == null
                    ? T("Recommended: no local model - {0}", ReadJsonString(rec, "Reason"))
                    : T("Recommended: {0} ({1}, runs on the {2}) - {3}", model.Label, model.Size, (gpu ? "graphics card" : "CPU"), ReadJsonString(rec, "Reason"));
            }
            llmRuntimeInstalled = ReadJsonBool(root, "RuntimeInstalled");
            llmRuntimeFlavor = ReadJsonString(root, "RuntimeFlavor");
            llmRuntimeName = ReadJsonString(root, "RuntimeName");
            llmRuntimeLicense = ReadJsonString(root, "RuntimeLicense") + " license " + ReadJsonString(root, "RuntimeLicenseUrl");
            llmRuntimeSizes.Clear();
            int runtimeMb = root.TryGetProperty("RuntimeApproxMb", out JsonElement mb) && mb.ValueKind == JsonValueKind.Number ? mb.GetInt32() : 0;
            llmRuntimeSizes[llmRuntimeFlavor] = runtimeMb;
            if (root.TryGetProperty("RuntimeSizesMb", out JsonElement sizes) && sizes.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty size in sizes.EnumerateObject())
                {
                    if (size.Value.ValueKind == JsonValueKind.Number)
                        llmRuntimeSizes[size.Name] = size.Value.GetInt32();
                }
            }
            llmRuntimeExtraLicenses.Clear();
            if (root.TryGetProperty("RuntimeExtraLicenses", out JsonElement extraLicenses) && extraLicenses.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty license in extraLicenses.EnumerateObject())
                {
                    if (license.Value.ValueKind == JsonValueKind.String)
                        llmRuntimeExtraLicenses[license.Name] = license.Value.GetString() ?? string.Empty;
                }
            }
            string choice = ReadJsonString(root, "RuntimeChoice");
            SelectedLlmRuntime = LlmRuntimeChoices.Contains(choice) ? choice : "auto";
            UpdateLlmRuntimeText();

            IsLlmUsingLocal = ReadJsonBool(root, "UsingLocal");
            string localServer = ReadJsonString(root, "LocalServer");
            string localModel = ReadJsonString(root, "LocalModel");
            if (localServer.Length > 0)
                LlmLocalServerPath = localServer;
            if (localModel.Length > 0)
                LlmLocalModelPath = localModel;
            bool ready = ReadJsonBool(root, "Ready");
            llmActiveModelId = selected;
            llmLocalReady = ReadJsonBool(root, "LocalReady");
            llmAnyModelInstalled = LlmModels.Any(m => m.Installed) && llmRuntimeInstalled;
            llmStatusLoaded = true;
            LlmReady = ready;
            LlmStatusText = IsLlmUsingLocal
                ? ready
                    ? T("Ready: your own model {0} through your llama.cpp.", Path.GetFileName(localModel))
                    : LlmEnabled ? T("On, but your llama-server.exe or GGUF file is missing.") : T("Off: the offline rules do the work (your own model is set up).")
                : ready
                    ? T("Ready: the downloaded model and llama.cpp are in place.")
                    : LlmEnabled
                        ? T("On, but the model is not downloaded yet.")
                        : T("Off: the offline rules do the work.");
        }
        catch (JsonException)
        {
        }
        finally
        {
            loadingLlmStatus = false;
        }
        OnLocalAiStatusChanged();
    }

    // Switching on keeps the user's own llama.cpp files when they are in use; else the model chosen in the list.
    internal List<string> BuildLlmEnableArguments(bool enabled)
    {
        List<string> args = ["llm", enabled ? "enable" : "disable"];
        if (enabled && !IsLlmUsingLocal && SelectedLlmModel != null)
            args.Add(SelectedLlmModel.Id);
        return args;
    }

    private async Task SetLlmEnabledAsync(bool enabled)
    {
        List<string> args = BuildLlmEnableArguments(enabled);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: enabled ? "Switching the local AI model on..." : "Switching the local AI model off...", forceText: true);
        if (result != null && result.ExitCode == 0)
            await RefreshLlmStatusAsync();
    }

    [RelayCommand]
    private async Task DownloadLlmModelAsync()
    {
        LlmModelItem? model = SelectedLlmModel;
        if (model == null)
        {
            LlmDownloadText = T("Choose a model first.");
            return;
        }
        if (!LlmLicenseAccepted)
        {
            LlmDownloadText = T("Tick \"I accept\" after reading size and license.");
            return;
        }
        if (IsLlmDownloading)
            return;
        string engine = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
        {
            LlmDownloadText = T("Engine path is missing.");
            return;
        }

        // Not queued behind other engine calls: a download takes minutes; the GUI follows its progress file.
        IsLlmDownloading = true;
        LlmDownloadPercent = 0;
        LlmDownloadText = T("Starting the download of {0}...", model.Label);
        try
        {
            // One click: runtime + model, checksums, and the model is switched on (engine llm download does all of it).
            List<string> downloadArgs = ["llm", "download", model.Id, "--accept-license"];
            if (SelectedLlmRuntime is { Length: > 0 } runtime && runtime != "auto")
                downloadArgs.AddRange(["--runtime", runtime]);
            downloadArgs.Add("--text");
            Task<EngineCommandResult> download = cliClient.RunAsync(engine, downloadArgs);
            while (!download.IsCompleted)
            {
                await Task.WhenAny(download, Task.Delay(500));
                ReadLlmProgress();
            }
            EngineCommandResult result = await download;
            ReadLlmProgress();
            LlmDownloadText = result.ExitCode == 0
                ? T("Downloaded and verified: {0}. The local AI model is on.", model.Label)
                : T("Download stopped: {0}", (result.StandardError + result.StandardOutput).Trim());
            if (result.ExitCode == 0)
                LlmDownloadPercent = 100;
        }
        catch (Exception ex)
        {
            LlmDownloadText = T("Download failed: {0}", ex.Message);
        }
        finally
        {
            IsLlmDownloading = false;
        }
        await RefreshLlmStatusAsync();
    }

    internal void ReadLlmProgress()
    {
        if (llmProgressFile.Length == 0 || !File.Exists(llmProgressFile))
            return;
        try
        {
            using FileStream stream = new(llmProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("Percent", out JsonElement percent) && percent.ValueKind == JsonValueKind.Number)
                LlmDownloadPercent = percent.GetDouble();
            string message = ReadJsonString(root, "Message");
            if (message.Length > 0)
                LlmDownloadText = message;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // The engine replaces the file while it writes; the next poll reads it.
        }
    }

    [RelayCommand]
    private void CancelLlmDownload()
    {
        if (!IsLlmDownloading || llmCancelFile.Length == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(llmCancelFile) ?? ".");
            File.WriteAllText(llmCancelFile, "cancel");
            LlmDownloadText = T("Stopping... (the part already downloaded is kept; Download resumes it)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LlmDownloadText = T("Could not stop the download: {0}", ex.Message);
        }
    }

    // Looks for a llama-server.exe and GGUF files already on this PC and fills in the pair that suits the hardware.
    [RelayCommand]
    private async Task FindLocalLlmAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["llm", "find-local"], includeProfile: false, mutatesProfile: false, busyLabel: N("Looking for llama.cpp and GGUF models on this PC..."), forceText: false);
        if (result == null)
            return;
        ApplyLocalScan(result.StandardOutput);
    }

    internal void ApplyLocalScan(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            string server = ReadJsonString(root, "BestServer");
            string model = ReadJsonString(root, "BestModel");
            int servers = root.TryGetProperty("Servers", out JsonElement s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
            int models = root.TryGetProperty("Models", out JsonElement m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;
            if (server.Length > 0)
                LlmLocalServerPath = server;
            if (model.Length > 0)
                LlmLocalModelPath = model;
            LlmLocalText = server.Length == 0 && model.Length == 0
                ? T("No llama-server.exe and no GGUF model found. Pick them with Browse, or download a model above.")
                : T("Found {0} llama-server.exe and {1} GGUF model(s). {2}{3}", servers.ToString(CultureInfo.InvariantCulture), models.ToString(CultureInfo.InvariantCulture), ReadJsonString(root, "Reason"), (server.Length == 0 ? T(" No llama-server.exe found: pick it with Browse.") : model.Length == 0 ? T(" No GGUF found: pick it with Browse.") : T(" Click 'Use these files'.")));
        }
        catch (JsonException)
        {
            LlmLocalText = output.Trim();
        }
    }

    internal List<string> BuildUseLocalArguments() =>
        ["llm", "use-local", "--server", LlmLocalServerPath.Trim().Trim('"'), "--model", LlmLocalModelPath.Trim().Trim('"')];

    // Uses the user's own llama.cpp and GGUF: nothing is downloaded, the model is switched on.
    [RelayCommand]
    private async Task UseLocalLlmAsync()
    {
        if (string.IsNullOrWhiteSpace(LlmLocalServerPath) || string.IsNullOrWhiteSpace(LlmLocalModelPath))
        {
            LlmLocalText = T("Choose your llama-server.exe and a .gguf model first (Find on this PC or Browse).");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(BuildUseLocalArguments(), includeProfile: false, mutatesProfile: false, busyLabel: N("Switching to your own model..."), forceText: true);
        if (result == null)
            return;
        LlmLocalText = result.ExitCode == 0 ? result.StandardOutput.Trim() : (result.StandardError + result.StandardOutput).Trim();
        await RefreshLlmStatusAsync();
    }

    [RelayCommand]
    private async Task ClearLocalLlmAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["llm", "use-local", "--clear"], includeProfile: false, mutatesProfile: false, busyLabel: N("Forgetting your own model..."), forceText: true);
        if (result == null)
            return;
        LlmLocalText = result.StandardOutput.Trim();
        LlmLocalServerPath = string.Empty;
        LlmLocalModelPath = string.Empty;
        await RefreshLlmStatusAsync();
    }

    [RelayCommand]
    private async Task RemoveLlmModelAsync()
    {
        LlmModelItem? model = SelectedLlmModel;
        if (model == null || IsLlmDownloading)
            return;
        EngineCommandResult? result = await RunEngineAsync(["llm", "remove", model.Id], includeProfile: false, mutatesProfile: false, busyLabel: "Removing " + model.Label + "...", forceText: true);
        if (result != null)
            LlmDownloadText = result.ExitCode == 0 ? T("Removed {0}.", model.Label) : result.StandardError.Trim();
        await RefreshLlmStatusAsync();
    }
}
