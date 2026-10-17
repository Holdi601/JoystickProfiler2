using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JoyPro.Ffb.Core.Runtime;

/// <summary>Waits until the next period of a fixed-rate loop without spinning and without raising the system timer
/// resolution.</summary>
/// <remarks>
/// On Windows 10 1803+ a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION) wakes the thread
/// within about a millisecond of the deadline; the old way (timeBeginPeriod(1) + Sleep(1) in a loop) raised the timer
/// interrupt rate of the whole machine for as long as the loop ran. Elsewhere it falls back to Thread.Sleep, which
/// rounds to the system tick (~15.6 ms). The timer is armed on the first <see cref="Wait"/> and disarmed by
/// <see cref="Pause"/>, so a paused loop costs nothing.
/// </remarks>
public abstract class PeriodicWaiter : IDisposable
{
    protected PeriodicWaiter(double periodSeconds) => Period = Math.Max(0.001, periodSeconds);

    public double Period { get; }

    /// <summary>True when the high-resolution timer is used.</summary>
    public abstract bool HighResolution { get; }

    public static PeriodicWaiter Create(double periodSeconds)
    {
        if (OperatingSystem.IsWindows())
        {
            var waiter = HighResolutionWaiter.TryCreate(periodSeconds);
            if (waiter is not null)
                return waiter;
        }

        return new SleepWaiter(periodSeconds);
    }

    /// <summary>Blocks until the next period starts (returns at once when the loop is behind).</summary>
    public abstract void Wait();

    /// <summary>Stops the timer until the next <see cref="Wait"/>.</summary>
    public abstract void Pause();

    public virtual void Dispose()
    {
    }

    private sealed class SleepWaiter(double period) : PeriodicWaiter(period)
    {
        private readonly Stopwatch _watch = new();
        private double _next;

        public override bool HighResolution => false;

        public override void Wait()
        {
            if (!_watch.IsRunning)
            {
                _watch.Restart();
                _next = 0;
            }

            _next += Period;
            var remaining = _next - _watch.Elapsed.TotalSeconds;
            if (remaining < -Period * 5)
                _next = _watch.Elapsed.TotalSeconds; // after a stall: no catch-up burst
            else if (remaining > 0)
                Thread.Sleep(TimeSpan.FromSeconds(remaining));
        }

        public override void Pause() => _watch.Reset();
    }

    private sealed class HighResolutionWaiter : PeriodicWaiter
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;
        private const uint Infinite = 0xFFFFFFFF;

        private readonly IntPtr _handle;
        private bool _armed;

        private HighResolutionWaiter(IntPtr handle, double period)
            : base(period) => _handle = handle;

        public override bool HighResolution => true;

        public static HighResolutionWaiter? TryCreate(double period)
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            return handle == IntPtr.Zero ? null : new HighResolutionWaiter(handle, period);
        }

        public override void Wait()
        {
            if (!_armed)
            {
                // Relative due time in 100 ns units (negative), then a periodic signal every Period (whole ms).
                var due = -(long)(Period * 10_000_000);
                var periodMs = Math.Max(1, (int)Math.Round(Period * 1000));
                if (!SetWaitableTimer(_handle, ref due, periodMs, IntPtr.Zero, IntPtr.Zero, false))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(Period));
                    return;
                }

                _armed = true;
            }

            WaitForSingleObject(_handle, Infinite);
        }

        public override void Pause()
        {
            if (!_armed)
                return;
            CancelWaitableTimer(_handle);
            _armed = false;
        }

        public override void Dispose()
        {
            Pause();
            CloseHandle(_handle);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelWaitableTimer(IntPtr timer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
