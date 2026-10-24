using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The local AI model's status (docs/natural-language-grouping.md): running or stopped, the loaded model and the memory
// it uses, "Stop now", and the stop after the idle minutes (default 30, persisted). The engine keeps llama-server loaded
// between requests only for this app (LlmServerMonitor.PublishOwner); the app stops it when idle and when it ends.
// The idle time only runs while JoyPro does no AI work: an engine command that uses the model (a request, an answer
// that streams in, a plan that is made or applied) holds it, and it starts again from the end of the last one.
public sealed partial class MainWindowViewModel
{
    private LlmServerMonitor llmServerMonitor = new(LlmServerMonitor.DefaultStateFile());
    private Timer? llmServerTimer;
    private int llmServerPolling;

    [ObservableProperty]
    private bool llmServerRunning;

    [ObservableProperty]
    private string llmServerStatusText = string.Empty;

    [ObservableProperty]
    private string llmServerChipText = string.Empty;

    public const decimal DefaultLlmIdleMinutes = 30m;
    private const double OldDefaultLlmIdleMinutes = 2;

    [ObservableProperty]
    private decimal llmIdleMinutes = DefaultLlmIdleMinutes;

    // ui-settings.json from before the 30-minute default stored the old default (2) with every save.
    private static bool IsOldLlmIdleDefault(UiSettingsState state) =>
        state.LlmIdleDefault == null && state.LlmIdleMinutes is double minutes && Math.Abs(minutes - OldDefaultLlmIdleMinutes) < 0.0001;

    // Engine commands that use the local AI model: the idle stop waits while one runs.
    internal static bool IsAiWorkCommand(IReadOnlyList<string> command)
    {
        if (command.Count >= 2 && string.Equals(command[0], "llm", StringComparison.OrdinalIgnoreCase))
            return string.Equals(command[1], "start", StringComparison.OrdinalIgnoreCase);
        if (command.Count >= 2 && string.Equals(command[0], "relations", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(command[1], "plan", StringComparison.OrdinalIgnoreCase) || string.Equals(command[1], "assistant", StringComparison.OrdinalIgnoreCase)))
            return true;
        for (int i = 0; i + 1 < command.Count; i++)
        {
            if (string.Equals(command[i], "--llm", StringComparison.OrdinalIgnoreCase))
                return !string.Equals(command[i + 1], "off", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // Tests: a monitor on their own state file.
    public void UseLlmServerMonitor(LlmServerMonitor monitor)
    {
        llmServerMonitor = monitor;
        monitor.IdleMinutes = (double)LlmIdleMinutes;
    }

    // The real app: engines it starts may keep the model loaded; a cheap check every 2 s (one small file).
    private void StartLlmServerWatch()
    {
        llmServerMonitor.IdleMinutes = (double)LlmIdleMinutes;
        LlmServerMonitor.PublishOwner((double)LlmIdleMinutes);
        llmServerTimer ??= new Timer(_ => Dispatcher.UIThread.Post(() => _ = PollLlmServerAsync()), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        ApplyLlmServerSnapshot(LlmServerSnapshot.Stopped);
    }

    partial void OnLlmIdleMinutesChanged(decimal value)
    {
        decimal minutes = Math.Clamp(value, 0.5m, 120m);
        if (minutes != value)
        {
            LlmIdleMinutes = minutes;
            return;
        }
        llmServerMonitor.IdleMinutes = (double)minutes;
        if (persistUiFiles)
            LlmServerMonitor.PublishOwner((double)minutes);
        SaveUiSettings();
    }

    public async Task PollLlmServerAsync()
    {
        if (Interlocked.Exchange(ref llmServerPolling, 1) == 1)
            return;
        try
        {
            // Started on the UI thread (the timer posts there), so the continuation is back on it.
            LlmServerSnapshot snapshot = await Task.Run(llmServerMonitor.Poll);
            ApplyLlmServerSnapshot(snapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref llmServerPolling, 0);
        }
    }

    [RelayCommand]
    private async Task StopLlmServerAsync()
    {
        bool stopped = await Task.Run(llmServerMonitor.StopNow);
        ApplyLlmServerSnapshot(LlmServerSnapshot.Stopped);
        StatusText = stopped ? T("The local AI model was stopped; its memory is free again.") : T("The local AI model is not running.");
    }

    // JoyPro ends: the model goes with it (also its job would end it).
    public void StopLlmServerOnExit()
    {
        llmServerTimer?.Dispose();
        llmServerTimer = null;
        try
        {
            llmServerMonitor.StopNow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
        }
    }

    // The same words as the status chip (MainWindowViewModel.LocalAi.cs): the chip near "Edit with AI" while loaded.
    private void ApplyLlmServerSnapshot(LlmServerSnapshot snapshot)
    {
        bool runningChanged = llmSnapshot.Running != snapshot.Running;
        llmSnapshot = snapshot;
        LlmServerRunning = snapshot.Running;
        if (runningChanged)
            OnAssistantModeChanged();
        LlmServerStatusText = AiStatusText;
        if (!snapshot.Running)
        {
            LlmServerChipText = T("AI: not loaded");
        }
        else
        {
            string memory = FormatLlmBytes(snapshot.MemoryBytes);
            TimeSpan left = TimeSpan.FromSeconds(Math.Ceiling(snapshot.IdleSecondsLeft));
            string leftText = left.ToString(left.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
            LlmServerChipText = snapshot.Busy ? T("AI: loaded · {0} · answering", memory) : T("AI: loaded · {0} · stops in {1}", memory, leftText);
        }
        OnLocalAiStatusChanged();
    }

    internal static string FormatLlmBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 MB";
        double gb = bytes / (1024.0 * 1024 * 1024);
        return gb >= 1
            ? gb.ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.CurrentCulture) + " MB";
    }
}
