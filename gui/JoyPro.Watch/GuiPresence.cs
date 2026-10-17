using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;


namespace JoyPro.Watch;

// Whether the full GUI runs, as the tray host sees it: the GUI's single-instance mutex exists while it runs; a GUI
// that starts writes its pid and sets a named event, and the tray host follows that process's Exited event. All event
// driven (one thread-pool wait on the event, the process wait), nothing polls.
public interface IGuiPresence : IDisposable
{
    bool IsRunning { get; }

    // Raised on a thread-pool thread.
    event Action? Started;

    event Action? Exited;

    void Start();
}

[SupportedOSPlatform("windows")]
public sealed class GuiPresence : IGuiPresence
{
    private readonly object sync = new();
    private readonly InstanceNames names;
    private EventWaitHandle? startedEvent;
    private RegisteredWaitHandle? wait;
    private Process? gui;
    private bool disposed;

    public GuiPresence(InstanceNames names)
    {
        this.names = names;
    }

    public event Action? Started;

    public event Action? Exited;

    public bool IsRunning => Mutex.TryOpenExisting(SingleInstanceGuard.MutexName(names.Gui), out Mutex? mutex) && Dispose(mutex);

    public void Start()
    {
        lock (sync)
        {
            if (disposed || startedEvent != null)
                return;
            startedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, names.GuiStartedEvent);
            wait = ThreadPool.RegisterWaitForSingleObject(startedEvent, OnStarted, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        if (IsRunning)
            Follow();
    }

    // Called by the GUI right after it became the one GUI: the tray host stops watching.
    public static void Announce(InstanceNames names)
    {
        try
        {
            Directory.CreateDirectory(names.SettingsFolder);
            File.WriteAllText(names.GuiPidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the pid the tray host still pauses (the event) and resumes when it is started again.
        }
        using EventWaitHandle started = new(false, EventResetMode.AutoReset, names.GuiStartedEvent);
        started.Set();
    }

    private void OnStarted(object? state, bool timedOut)
    {
        // A stale signal (the GUI started and ended while no tray host ran) is no GUI.
        if (IsRunning)
            Follow();
    }

    private void Follow()
    {
        Process? process = null;
        try
        {
            int pid = int.Parse(File.ReadAllText(names.GuiPidFile).Trim(), CultureInfo.InvariantCulture);
            process = Process.GetProcessById(pid);
            lock (sync)
            {
                if (disposed || (gui != null && gui.Id == pid))
                {
                    process.Dispose();
                    return;
                }
                ReleaseProcess();
                process.EnableRaisingEvents = true;
                process.Exited += OnGuiExited;
                gui = process;
                if (process.HasExited)
                {
                    ReleaseProcess();
                    process = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            process = null;
        }
        Started?.Invoke();
        if (process == null && !IsRunning)
            Exited?.Invoke();
    }

    private void OnGuiExited(object? sender, EventArgs e)
    {
        lock (sync)
        {
            if (disposed || !ReferenceEquals(sender, gui))
                return;
            ReleaseProcess();
        }
        Exited?.Invoke();
    }

    private void ReleaseProcess()
    {
        if (gui == null)
            return;
        gui.Exited -= OnGuiExited;
        gui.Dispose();
        gui = null;
    }

    private static bool Dispose(Mutex mutex)
    {
        mutex.Dispose();
        return true;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            wait?.Unregister(null);
            wait = null;
            startedEvent?.Dispose();
            startedEvent = null;
            ReleaseProcess();
        }
        Started = null;
        Exited = null;
    }
}
