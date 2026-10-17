using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The live busy area of engine commands (BusyProgressViewModel): the engine runs with --status true, its stderr lines
// come in while it runs (IEngineCliClient.RunStreamingAsync) and are handed to the UI thread in batches; a clock ticks
// every second. Cancel asks the engine to stop through JOYPRO_CANCEL_FILE: the engine offers it only while it reads
// and stops before it writes ([engine-cancel], exit code 3), so nothing is ever killed half-way.
public sealed partial class MainWindowViewModel
{
    // The engine's exit code of a command the user cancelled (engine/EngineProgress.cs).
    public const int EngineCancelledExitCode = 3;

    private readonly ConcurrentQueue<string> engineStderrLines = new();
    private int engineStderrDrainQueued;
    private SynchronizationContext? engineProgressContext;
    private Timer? busyClock;
    private string? engineCancelFile;
    private bool engineCancelRequested;

    public BusyProgressViewModel BusyProgress { get; } = new();

    // Part of a batch of engine calls ("Command list 2 of 5: MSFS 2024"); shown above the step.
    private string busyBatchText = string.Empty;

    // The last foreground engine call ended because the user cancelled it.
    internal bool LastEngineCallCancelled { get; private set; }

    // A foreground engine call starts: the busy area shows its label, the clock runs, cancel is prepared.
    private Dictionary<string, string> BeginBusyProgress(string busyLabel)
    {
        while (engineStderrLines.TryDequeue(out _))
        {
        }
        engineProgressContext = SynchronizationContext.Current;
        engineCancelRequested = false;
        LastEngineCallCancelled = false;
        engineCancelFile = Path.Combine(Path.GetTempPath(), "JoyPro2", "cancel", Guid.NewGuid().ToString("N") + ".request");
        BusyProgress.CancelCommand ??= CancelEngineRunCommand;
        BusyProgress.Begin(busyLabel, DateTime.Now, busyBatchText);
        BusyProgress.OnStartPage = IsStartStage;
        StartBusyClock();
        return new Dictionary<string, string> { [EngineCancelFileVariable] = engineCancelFile };
    }

    private const string EngineCancelFileVariable = "JOYPRO_CANCEL_FILE";

    // The call returned: the remaining lines are shown, the cancel file is removed.
    private void FinishBusyProgress(EngineCommandResult? result)
    {
        DrainEngineStderr();
        LastEngineCallCancelled = engineCancelRequested && result != null && result.ExitCode != 0;
        StopBusyClock();
        BusyProgress.Tick(DateTime.Now);
        string? file = engineCancelFile;
        engineCancelFile = null;
        if (file != null)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // No engine call is running or waiting any more.
    private void EndBusyProgress()
    {
        StopBusyClock();
        BusyProgress.End();
    }

    // Called on the engine's output thread for every stderr line.
    private void OnEngineStderrLine(string line)
    {
        engineStderrLines.Enqueue(line);
        if (Interlocked.Exchange(ref engineStderrDrainQueued, 1) != 0)
            return;
        SynchronizationContext? context = engineProgressContext;
        if (context != null)
            context.Post(_ => DrainEngineStderr(), null);
        else
            DrainEngineStderr();
    }

    private void DrainEngineStderr()
    {
        Volatile.Write(ref engineStderrDrainQueued, 0);
        List<string> lines = [];
        while (engineStderrLines.TryDequeue(out string? line))
            lines.Add(line);
        if (lines.Count > 0)
            BusyProgress.Apply(lines, DateTime.Now);
    }

    private void StartBusyClock()
    {
        StopBusyClock();
        SynchronizationContext? context = engineProgressContext;
        busyClock = new Timer(_ =>
        {
            if (context != null)
                context.Post(__ => { if (BusyProgress.IsActive) BusyProgress.Tick(DateTime.Now); }, null);
            else if (BusyProgress.IsActive)
                BusyProgress.Tick(DateTime.Now);
        }, null, 1000, 1000);
    }

    private void StopBusyClock()
    {
        busyClock?.Dispose();
        busyClock = null;
    }

    // Cancel in the busy area: only when the engine offers it (it reads and has not written anything yet).
    [RelayCommand]
    private void CancelEngineRun()
    {
        string? file = engineCancelFile;
        if (!BusyProgress.CanCancel || file == null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "cancel");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Could not ask the engine to stop: {0}", ex.Message);
            return;
        }
        engineCancelRequested = true;
        BusyProgress.MarkCancelling(DateTime.Now);
        StatusText = T("Cancelling... JoyPro stops before it writes anything.");
    }
}
