using System.Diagnostics;
using System.Globalization;
using JoyPro.Engine.VJoy;

namespace JoyPro.VJoy;

/// <summary>
/// JoyPro.VJoy bench [--functions 50] [--rate 240] [--seconds 10] [--sources 4]: the forwarding loop without devices.
/// Synthetic sources change every pass (every axis moves, buttons and hats toggle), the loop runs on the same
/// high-resolution waitable timer as the forwarder, applies the table, evaluates the virtual functions, merges them and
/// writes the dry sink when a position changed. It reports the work per pass (what a pass adds to the input latency),
/// the timer's lateness, the CPU share of one core and the loop thread's allocations. Nothing touches a device, vJoy,
/// the Xbox pad or the keyboard.
/// </summary>
internal static class Bench
{
    public static int Run(int functions, int rateHz, int seconds, int sources, Action<string> output)
    {
        rateHz = Math.Clamp(rateHz, 30, 1000);
        seconds = Math.Clamp(seconds, 1, 600);
        sources = Math.Clamp(sources, 1, 16);
        output("bench functions=" + functions.ToString(CultureInfo.InvariantCulture) + " rate=" + rateHz.ToString(CultureInfo.InvariantCulture) +
               " seconds=" + seconds.ToString(CultureInfo.InvariantCulture) + " sources=" + sources.ToString(CultureInfo.InvariantCulture));
        var baseline = Measure(0, rateHz, seconds, sources);
        output(baseline.Describe("table only"));
        var loaded = Measure(functions, rateHz, seconds, sources);
        output(loaded.Describe("with functions"));
        output("result functions=" + functions.ToString(CultureInfo.InvariantCulture) + " rate=" + rateHz.ToString(CultureInfo.InvariantCulture) +
               " work-us-avg=" + loaded.AvgWorkUs.ToString("0.0", CultureInfo.InvariantCulture) + " work-us-p99=" + loaded.P99WorkUs.ToString("0.0", CultureInfo.InvariantCulture) +
               " work-us-max=" + loaded.MaxWorkUs.ToString("0.0", CultureInfo.InvariantCulture) + " cpu-percent=" + loaded.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture) +
               " cpu-percent-table-only=" + baseline.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture) + " loop-alloc=" + loaded.LoopAllocated.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    public sealed class Result
    {
        public int Functions;
        public long Passes;
        public double Seconds;
        public double AvgWorkUs;
        public double P99WorkUs;
        public double MaxWorkUs;
        public double AvgLateUs;
        public double MaxLateUs;
        public double CpuPercent;
        public long Writes;
        public long LoopAllocated;

        public string Describe(string label) =>
            "pass " + label + ": passes=" + Passes.ToString(CultureInfo.InvariantCulture) +
            " hz=" + (Passes / Math.Max(0.001, Seconds)).ToString("0", CultureInfo.InvariantCulture) +
            " work-us avg=" + AvgWorkUs.ToString("0.0", CultureInfo.InvariantCulture) + " p99=" + P99WorkUs.ToString("0.0", CultureInfo.InvariantCulture) + " max=" + MaxWorkUs.ToString("0.0", CultureInfo.InvariantCulture) +
            " timer-late-us avg=" + AvgLateUs.ToString("0", CultureInfo.InvariantCulture) + " max=" + MaxLateUs.ToString("0", CultureInfo.InvariantCulture) +
            " cpu=" + CpuPercent.ToString("0.00", CultureInfo.InvariantCulture) + "% writes=" + Writes.ToString(CultureInfo.InvariantCulture) +
            " loop-alloc=" + LoopAllocated.ToString(CultureInfo.InvariantCulture);
    }

    // One measured run on its own thread (the forwarder's loop runs on its own thread, too).
    public static Result Measure(int functions, int rateHz, int seconds, int sources)
    {
        Result? result = null;
        var thread = new Thread(() => result = MeasureOnThread(functions, rateHz, seconds, sources)) { Name = "JoyPro vJoy bench", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        thread.Join();
        return result!;
    }

    private static unsafe Result MeasureOnThread(int functions, int rateHz, int seconds, int sources)
    {
        var config = new ForwarderConfig
        {
            VJoyDevices = [1],
            Sources = Enumerable.Range(0, sources).Select(i => new ForwarderSource("BENCH" + i.ToString(CultureInfo.InvariantCulture), "BENCH", Guid.Empty)).ToList(),
            Functions = VfBenchmark.Mix(functions, sources)
        };
        // The table forwards each source's 8 axes and first 16 buttons and a hat (8 of the axes fit on vJoy).
        for (var s = 0; s < sources; s++)
        {
            if (s == 0)
            {
                for (var a = 0; a < 8; a++)
                    config.Slots.Add(new ForwarderSlot(s, "axis", a, 1, a));
            }

            for (var b = 0; b < 8; b++)
                config.Slots.Add(new ForwarderSlot(s, "button", b, 1, s * 8 + b));
            if (s < 4)
                config.Slots.Add(new ForwarderSlot(s, "hat", 0, 1, s));
        }

        var sink = new DrySink();
        var table = new ForwardingTable(config);
        var runtime = config.Functions.Count > 0 ? new VfRuntime(config.Functions, sources, table.DeviceIds.Length, 0, 32767, new DryKeySink()) : null;
        var states = Enumerable.Range(0, sources).Select(_ => new byte[ForwardingTable.StateSize]).ToArray();
        foreach (var state in states)
            VfRuntime.Neutral(state);
        var sent = new VJoyPosition[table.DeviceIds.Length];
        var periodMs = Math.Max(1, 1000 / rateHz);
        var capacity = seconds * (1000 / periodMs) + 64;
        var work = new long[capacity];
        var late = new long[capacity];
        var timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, Native.CreateWaitableTimerHighResolution, Native.TimerAllAccess);
        var highResolution = timer != IntPtr.Zero;
        if (!highResolution)
        {
            timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, Native.TimerAllAccess);
            Native.timeBeginPeriod(1);
        }

        // The first passes warm up (JIT, tiering) and are not counted.
        const int warmup = 64;
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var allocStart = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var measureStart = start;
        var periodTicks = Stopwatch.Frequency * periodMs / 1000;
        var end = start + Stopwatch.Frequency * seconds;
        long due = -(long)periodMs * 10_000;
        Native.SetWaitableTimer(timer, ref due, periodMs, IntPtr.Zero, IntPtr.Zero, false);
        var handle = timer;
        long passes = 0;
        long counted = 0;
        long writes = 0;
        long lastWoke = 0;
        var microsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        while (true)
        {
            Native.WaitForMultipleObjects(1, &handle, false, Native.Infinite);
            var woke = Stopwatch.GetTimestamp();
            if (woke >= end + Stopwatch.Frequency || counted >= capacity)
                break;
            if (passes == warmup)
            {
                process.Refresh();
                cpuStart = process.TotalProcessorTime;
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                measureStart = woke;
                end = woke + Stopwatch.Frequency * seconds;
                writes = 0;
            }

            var measured = passes >= warmup;
            if (measured && woke >= end)
                break;
            // Timer lateness: how much later than one period after the previous wake-up this one came.
            if (measured && lastWoke > 0)
                late[counted] = Math.Max(0, woke - lastWoke - periodTicks);
            lastWoke = woke;
            // The work of one pass: new device states, table, functions, merge, compare, write.
            for (var s = 0; s < sources; s++)
            {
                VfBenchmark.Drive(states[s], s, passes, 0, 32767);
                table.Apply(s, states[s]);
                if (runtime is not null)
                    Buffer.BlockCopy(states[s], 0, runtime.State(s)!, 0, ForwardingTable.StateSize);
            }

            if (runtime is not null)
            {
                runtime.Tick((long)((woke - start) * microsPerTick));
                table.MergeFunctions(runtime);
            }

            for (var d = 0; d < sent.Length; d++)
            {
                ref var position = ref table.Position(d);
                if (!Same(ref position, ref sent[d]))
                {
                    sink.Update(table.DeviceIds[d], ref position);
                    sent[d] = position;
                    writes++;
                }
            }

            if (measured)
                work[counted++] = Stopwatch.GetTimestamp() - woke;
            passes++;
        }

        var elapsed = (Stopwatch.GetTimestamp() - measureStart) / (double)Stopwatch.Frequency;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        Native.CancelWaitableTimer(timer);
        Native.CloseHandle(timer);
        if (!highResolution)
            Native.timeEndPeriod(1);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        var sorted = work.Take((int)counted).OrderBy(w => w).ToArray();
        return new Result
        {
            Functions = functions,
            Passes = counted,
            Seconds = elapsed,
            AvgWorkUs = counted == 0 ? 0 : sorted.Average() * microsPerTick,
            P99WorkUs = counted == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.99))] * microsPerTick,
            MaxWorkUs = counted == 0 ? 0 : sorted[^1] * microsPerTick,
            AvgLateUs = counted == 0 ? 0 : late.Take((int)counted).Average() * microsPerTick,
            MaxLateUs = counted == 0 ? 0 : late.Take((int)counted).Max() * microsPerTick,
            CpuPercent = cpu / Math.Max(0.001, elapsed) * 100.0,
            Writes = writes,
            LoopAllocated = allocated
        };
    }

    private static bool Same(ref VJoyPosition a, ref VJoyPosition b) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref a, 1))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref b, 1)));
}
