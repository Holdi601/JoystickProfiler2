using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The local AI model's status (docs/natural-language-grouping.md): running or stopped, the loaded model and the memory
// it uses, "Stop now", and the stop after the idle minutes (default 2, persisted). The engine keeps llama-server loaded
// between requests only for this app (LlmServerMonitor.PublishOwner); the app stops it when idle and when it ends.
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

    [ObservableProperty]
    private decimal llmIdleMinutes = 2;

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

    private void ApplyLlmServerSnapshot(LlmServerSnapshot snapshot)
    {
        LlmServerRunning = snapshot.Running;
        if (!snapshot.Running)
        {
            LlmServerStatusText = T("AI model: stopped (no memory in use).");
            LlmServerChipText = T("AI stopped");
            return;
        }
        string memory = FormatLlmBytes(snapshot.MemoryBytes);
        string memoryText = snapshot.MemoryIsVram ? T("{0} video memory", memory) : T("{0} memory", memory);
        TimeSpan left = TimeSpan.FromSeconds(Math.Ceiling(snapshot.IdleSecondsLeft));
        string leftText = left.ToString(left.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
        LlmServerStatusText = snapshot.Busy
            ? T("AI model: running {0} ({1}), answering a request.", snapshot.Model, memoryText)
            : T("AI model: running {0} ({1}), stops in {2} without a request.", snapshot.Model, memoryText, leftText);
        LlmServerChipText = snapshot.Busy ? T("AI running · {0}", memory) : T("AI running · {0} · stops in {1}", memory, leftText);
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
