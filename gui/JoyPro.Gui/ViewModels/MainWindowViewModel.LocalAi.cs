using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The local AI in one place (docs/natural-language-grouping.md, docs/ai-plans.md): Settings > Local AI holds the whole
// setup (on/off, model or own llama.cpp files, runtime, download, Start now / Stop now, idle stop); every AI action of
// the app (Manage with AI, Edit with AI, Expand with AI, the JoyPro assistant, Group by description, AI fill) shows the
// same one-line status chip (Views/Controls/LocalAiStatusChip.axaml) with two parts: whether the AI is switched on
// (the setting) and whether the model is loaded right now. The status is read from the engine (llm status) when an AI
// panel opens and before an AI request, so a request never goes out with a stale "off".
public sealed partial class MainWindowViewModel
{
    public const string LocalAiSectionId = "local-ai";

    private bool llmStatusLoaded;
    private Task? llmStatusTask;
    private string llmActiveModelId = string.Empty;
    private bool llmLocalReady;
    private bool llmAnyModelInstalled;
    private LlmServerSnapshot llmSnapshot = LlmServerSnapshot.Stopped;

    // An AI request or Start now is loading the model.
    [ObservableProperty]
    private bool isLlmStarting;

    // The setting has been read from the engine (before that the chip says "checking").
    public bool IsLlmStatusKnown => llmStatusLoaded;

    // The model the AI uses: the downloaded one ("Qwen3 8B") or the user's own GGUF file.
    public string AiModelName
    {
        get
        {
            // The model of the setting; before it is read, the one that runs.
            if (!llmStatusLoaded && llmSnapshot.Running && llmSnapshot.Model.Length > 0)
                return ShortModelName(llmSnapshot.Model);
            if (IsLlmUsingLocal)
                return LlmLocalModelPath.Length > 0 ? Path.GetFileNameWithoutExtension(LlmLocalModelPath) : T("your own model");
            LlmModelItem? active = LlmModels.FirstOrDefault(m => string.Equals(m.Id, llmActiveModelId, StringComparison.OrdinalIgnoreCase));
            return active != null ? ShortModelName(active.Label) : string.Empty;
        }
    }

    // "Qwen3 8B (Q4_K_M, non-thinking)" -> "Qwen3 8B"; "Your model: x.gguf" -> "x".
    internal static string ShortModelName(string label)
    {
        string name = (label ?? string.Empty).Trim();
        if (name.StartsWith("Your model:", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name["Your model:".Length..].Trim());
        int bracket = name.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    // A model JoyPro can start once the AI is on: a downloaded model with its llama.cpp, or the user's own files.
    public bool IsAiSetUp => llmAnyModelInstalled || (IsLlmUsingLocal && llmLocalReady);

    // Why the switched-on AI cannot run yet (empty when it can).
    public string AiNotReadyReason => !LlmEnabled || LlmReady ? string.Empty
        : IsLlmUsingLocal ? T("your llama-server.exe or GGUF file is missing")
        : !LlmModels.Any(m => m.Installed && string.Equals(m.Id, llmActiveModelId, StringComparison.OrdinalIgnoreCase)) ? T("the model is not downloaded yet")
        : T("llama.cpp is not downloaded yet");

    // One line, the same everywhere: on/off (the setting), then loaded or not (right now).
    public string AiStatusText
    {
        get
        {
            string name = AiModelName;
            string on = name.Length > 0 ? T("AI: on ({0})", name) : T("AI: on");
            if (!llmSnapshot.Running)
            {
                if (!llmStatusLoaded)
                    return IsLlmStarting ? T("AI: starting the AI model...") : T("AI: checking...");
                if (!LlmEnabled)
                    return T("AI: off");
                if (!LlmReady)
                    return T("{0} - not ready: {1}", on, AiNotReadyReason);
                if (IsLlmStarting)
                    return T("{0} - starting the AI model...", on);
                return T("{0} - not loaded right now, starts with your next request", on);
            }
            string memory = FormatLlmBytes(llmSnapshot.MemoryBytes);
            if (llmSnapshot.Busy)
                return T("{0} - loaded, {1}, answering a request", on, memory);
            TimeSpan left = TimeSpan.FromSeconds(Math.Ceiling(llmSnapshot.IdleSecondsLeft));
            return T("{0} - loaded, {1}, stops in {2}", on, memory, left.ToString(left.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture));
        }
    }

    // The dot of the chip: green loaded, accent on and ready, amber on but not ready, gray off.
    public bool IsAiDotLoaded => llmSnapshot.Running;
    public bool IsAiDotReady => !llmSnapshot.Running && llmStatusLoaded && LlmEnabled && LlmReady;
    public bool IsAiDotProblem => !llmSnapshot.Running && llmStatusLoaded && LlmEnabled && !LlmReady;
    public bool IsAiDotOff => !llmSnapshot.Running && (!llmStatusLoaded || !LlmEnabled);
    public bool IsAiKnownOff => llmStatusLoaded && !LlmEnabled;

    // The chip's offers: off and set up -> Turn on; not set up (or on but not ready) -> Set up.
    public bool ShowAiTurnOn => llmStatusLoaded && !LlmEnabled && IsAiSetUp;
    public bool ShowAiSetUp => llmStatusLoaded && !llmSnapshot.Running && (!LlmEnabled && !IsAiSetUp || LlmEnabled && !LlmReady);
    public bool CanStartLlmNow => llmStatusLoaded && LlmEnabled && LlmReady && !llmSnapshot.Running && !IsLlmStarting;

    // Settings > Local AI: the files found on this PC are not the ones in use yet.
    public bool HasUnusedLocalFiles => LlmLocalServerPath.Trim().Length > 0 && LlmLocalModelPath.Trim().Length > 0 &&
                                       (!IsLlmUsingLocal || !string.Equals(LlmLocalModelPath.Trim().Trim('"'), llmLocalModelInUse, StringComparison.OrdinalIgnoreCase) ||
                                        !string.Equals(LlmLocalServerPath.Trim().Trim('"'), llmLocalServerInUse, StringComparison.OrdinalIgnoreCase));

    private string llmLocalServerInUse = string.Empty;
    private string llmLocalModelInUse = string.Empty;

    // Which model the next request uses, for Settings > Local AI.
    public string AiModelInUseText => !llmStatusLoaded ? string.Empty
        : IsLlmUsingLocal ? T("In use: your own llama.cpp with {0} ({1}).", Path.GetFileName(llmLocalModelInUse), llmLocalServerInUse)
        : AiModelName.Length > 0 ? T("In use: {0}, downloaded by JoyPro.", AiModelName)
        : T("No model chosen yet: download one below or use your own llama.cpp and GGUF file.");

    partial void OnIsLlmStartingChanged(bool value) => OnLocalAiStatusChanged();

    partial void OnLlmLocalServerPathChanged(string value) => OnPropertyChanged(nameof(HasUnusedLocalFiles));

    partial void OnLlmLocalModelPathChanged(string value) => OnPropertyChanged(nameof(HasUnusedLocalFiles));

    internal void OnLocalAiStatusChanged()
    {
        if (IsLlmUsingLocal && llmStatusLoaded)
        {
            llmLocalServerInUse = LlmLocalServerPath.Trim().Trim('"');
            llmLocalModelInUse = LlmLocalModelPath.Trim().Trim('"');
        }
        foreach (string name in new[]
                 {
                     nameof(IsLlmStatusKnown), nameof(AiModelName), nameof(IsAiSetUp), nameof(AiNotReadyReason), nameof(AiStatusText), nameof(IsAiDotLoaded), nameof(IsAiDotReady),
                     nameof(IsAiDotProblem), nameof(IsAiDotOff), nameof(IsAiKnownOff), nameof(ShowAiTurnOn), nameof(ShowAiSetUp), nameof(CanStartLlmNow), nameof(HasUnusedLocalFiles), nameof(AiModelInUseText),
                     nameof(LocalAiComponentReady), nameof(LocalAiComponentText)
                 })
            OnPropertyChanged(name);
        LlmServerStatusText = AiStatusText;
    }

    // The engine's setting, read once (an AI panel opens, Settings > Local AI, before a request). Every caller waits for
    // the same read.
    public Task EnsureLlmStatusAsync()
    {
        if (llmStatusLoaded)
            return Task.CompletedTask;
        if (llmStatusTask is { IsCompleted: true })
            llmStatusTask = null;
        return llmStatusTask ??= LoadLlmStatusQuietlyAsync();
    }

    private async Task LoadLlmStatusQuietlyAsync()
    {
        try
        {
            EngineCommandResult? result = await RunEngineAsync(["llm", "status"], includeProfile: false, mutatesProfile: false, busyLabel: N("Checking the local AI model..."),
                forceText: false, background: true);
            if (result != null && result.ExitCode == 0)
                ApplyLlmStatus(result.StandardOutput);
            // Asked once: an engine without an answer is not asked again for every request.
            if (result != null && !llmStatusLoaded)
            {
                llmStatusLoaded = true;
                OnLocalAiStatusChanged();
            }
        }
        finally
        {
            llmStatusTask = null;
        }
    }

    // Settings > Local AI, opened and scrolled to (the chip's Settings link, Components > Set up, Set up the local AI).
    [RelayCommand]
    private async Task OpenLocalAiSettingsAsync()
    {
        OpenSettingsSection(LocalAiSectionId);
        if (LlmModels.Count == 0)
            await RefreshLlmStatusAsync();
    }

    // The chip's "Turn on the local AI": the model that is set up is switched on (one click).
    [RelayCommand]
    private async Task TurnOnLocalAiAsync()
    {
        await EnsureLlmStatusAsync();
        if (!IsAiSetUp)
        {
            await OpenLocalAiSettingsAsync();
            return;
        }
        if (!LlmEnabled)
            LlmEnabled = true; // OnLlmEnabledChanged: engine llm enable, then the status again
        StatusText = T("The local AI is on.");
    }

    // Start now: the model loads and stays loaded for the next requests (until the idle stop).
    [RelayCommand]
    private async Task StartLlmServerAsync()
    {
        await EnsureLlmStatusAsync();
        if (!LlmEnabled || !LlmReady)
        {
            StatusText = LlmEnabled ? T("The local AI cannot start: {0}.", AiNotReadyReason) : T("Switch the local AI on first.");
            return;
        }
        IsLlmStarting = true;
        try
        {
            EngineCommandResult? result = await RunEngineAsync(["llm", "start"], includeProfile: false, mutatesProfile: false,
                busyLabel: T("Starting the AI model ({0})...", AiModelName), forceText: true);
            if (result == null)
                return;
            LlmStartText = result.ExitCode == 0 ? result.StandardOutput.Trim() : (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
            StatusText = result.ExitCode == 0 ? T("The local AI model is loaded.") : LlmStartText;
        }
        finally
        {
            IsLlmStarting = false;
        }
        await PollLlmServerAsync();
    }

    // The last Start now's answer (the reason when the model did not start).
    [ObservableProperty]
    private string llmStartText = string.Empty;

    // Settings > Components: the local AI's card says the same as the components' cards.
    public bool LocalAiComponentReady => llmStatusLoaded && LlmEnabled && LlmReady;

    public string LocalAiComponentText => !llmStatusLoaded ? T("Not checked yet.")
        : LocalAiComponentReady ? T("Set up and on: {0}.", AiModelName)
        : LlmEnabled ? T("On, but not ready: {0}.", AiNotReadyReason)
        : IsAiSetUp ? T("Set up ({0}), switched off.", AiModelName)
        : T("Not set up (optional).");
}
