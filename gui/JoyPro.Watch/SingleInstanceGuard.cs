using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JoyPro.Watch;

// One JoyPro2 per user and settings folder: a named mutex says one runs, a named event asks it to come to the front.
// The running copy waits for the event on the thread pool (no thread of its own, no polling). A second start without
// --tray signals the event and exits; one with --tray (autostart while JoyPro2 already runs) just exits.
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly object sync = new();
    private readonly Mutex mutex;
    private readonly EventWaitHandle activate;
    private RegisteredWaitHandle? wait;
    private bool disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate)
    {
        this.mutex = mutex;
        this.activate = activate;
        wait = ThreadPool.RegisterWaitForSingleObject(activate, OnActivate, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // Raised on a thread-pool thread when another start asked this one to show its window.
    public event Action? ActivationRequested;

    // "Local\" names are per Windows session. The settings folder is part of the name, so a test run with its own
    // JOYPRO_DATA_DIR never meets the user's JoyPro2.
    public static string NameFor(string settingsFolder) => InstanceNames.GuiNameFor(settingsFolder);

    // The guard when this is the first JoyPro2, null when another one runs.
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        // The event first: a second start that signals before the first one waits still reaches it (it stays set).
        EventWaitHandle activate = new(false, EventResetMode.AutoReset, EventName(name));
        Mutex mutex = new(false, MutexName(name), out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            activate.Dispose();
            return null;
        }
        return new SingleInstanceGuard(mutex, activate);
    }

    // Asks the running JoyPro2 to come to the front; false when none runs.
    public static bool SignalExisting(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(name), out EventWaitHandle? activate))
            return false;
        using (activate)
        {
            // This start is the foreground process (the user started it): let the running one take the foreground.
            try { AllowSetForegroundWindow(AsfwAny); } catch (Exception) { }
            return activate.Set();
        }
    }

    private void OnActivate(object? state, bool timedOut)
    {
        lock (sync)
        {
            if (disposed)
                return;
        }
        try
        {
            ActivationRequested?.Invoke();
        }
        catch (Exception)
        {
            // A failing handler must not take the thread-pool thread down.
        }
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
        }
        ActivationRequested = null;
        activate.Dispose();
        mutex.Dispose();
    }

    public static string MutexName(string name) => @"Local\" + name + ".Running";

    public static string EventName(string name) => @"Local\" + name + ".Activate";

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
