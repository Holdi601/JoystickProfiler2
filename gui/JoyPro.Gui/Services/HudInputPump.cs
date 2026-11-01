using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace JoyPro.Gui.Services;

// Takes the axis reports of the input monitor (a background thread, about 66 reports a second per moving device) to
// the UI thread for the HUD (docs/hud.md "Performance"): only the newest report of each device is kept, and they are
// applied at most MaxFps times a second in one go, followed by one "flushed" call (the aircraft widget moves, the
// stream page gets its state). Nothing is allocated per report or per update: the reports wait in a dictionary that
// is swapped with a second one, the UI-thread callbacks are made once, and one timer waits out the rest of a frame.
public sealed class HudInputPump
{
    private readonly object gate = new();
    private Dictionary<string, JoystickAxisSnapshot> pending = new(StringComparer.Ordinal);
    private Dictionary<string, JoystickAxisSnapshot> applying = new(StringComparer.Ordinal);
    private readonly Action<JoystickAxisSnapshot> apply;
    private readonly Action flushed;
    private readonly Action onUiThread;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private DispatcherTimer? timer;
    private long lastFlushMs = -1000;
    private bool posted;

    public HudInputPump(Action<JoystickAxisSnapshot> apply, Action flushed)
    {
        this.apply = apply;
        this.flushed = flushed;
        onUiThread = OnUiThread;
    }

    // Updates per second at most (15, 30 or 60; the HUD's setting).
    public int MaxFps { get; set; } = 30;

    // How many updates were applied (tests, the soak measurement).
    public long Flushes { get; private set; }

    private int IntervalMs => 1000 / Math.Clamp(MaxFps, 5, 120);

    // From any thread: the device's newest axes.
    public void Offer(JoystickAxisSnapshot snapshot)
    {
        lock (gate)
        {
            pending[snapshot.Device] = snapshot;
            if (posted)
                return;
            posted = true;
        }
        Dispatcher.UIThread.Post(onUiThread, DispatcherPriority.Background);
    }

    // Something else changed (a button, a line of the binds ticker): one update without new axes, at the same rate.
    public void Kick()
    {
        lock (gate)
        {
            if (posted)
                return;
            posted = true;
        }
        if (Dispatcher.UIThread.CheckAccess())
            OnUiThread();
        else
            Dispatcher.UIThread.Post(onUiThread, DispatcherPriority.Background);
    }

    private void OnUiThread()
    {
        long wait = lastFlushMs + IntervalMs - clock.ElapsedMilliseconds;
        if (wait > 1)
        {
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background);
                timer.Tick += OnTimer;
            }
            if (!timer.IsEnabled)
            {
                timer.Interval = TimeSpan.FromMilliseconds(wait);
                timer.Start();
            }
            return;
        }
        Flush();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        timer?.Stop();
        Flush();
    }

    // Applies what came in since the last update (also directly from tests).
    public void Flush()
    {
        lock (gate)
        {
            (pending, applying) = (applying, pending);
            posted = false;
        }
        foreach (JoystickAxisSnapshot snapshot in applying.Values)
            apply(snapshot);
        applying.Clear();
        lastFlushMs = clock.ElapsedMilliseconds;
        Flushes++;
        flushed();
    }

    public void Stop()
    {
        timer?.Stop();
        lock (gate)
        {
            pending.Clear();
            posted = false;
        }
    }
}
