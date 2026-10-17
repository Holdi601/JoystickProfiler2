using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using JoyPro.Engine.VJoy;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.VJoy;

/// <summary>JoyPro's virtual joystick forwarder (docs/virtual-joystick.md).</summary>
/// <remarks>
/// JoyPro.VJoy run --config &lt;file&gt; [--sink vjoy|dry] [--rate Hz] [--parent-pid N] [--protocol] [--monitor]
///                 [--duration seconds] [--vjoy-dll path]
///     forwards the mapped physical inputs (engine vjoy forwarder-config) to the vJoy devices until "stop" (stdin),
///     stdin ends (--protocol), the parent exits, Ctrl+C or --duration. The default sink is dry (nothing is written to
///     vJoy); only --sink vjoy touches the vJoy devices. JOYPRO_VJOY_DRY=1 forces dry (tests).
/// JoyPro.VJoy devices [--json]
///     the DirectInput game controllers with instance GUID and HID path (read-only: nothing is acquired)
/// JoyPro.VJoy refresh-rate
///     the highest display refresh rate (at least 60), the default forwarding rate
/// </remarks>
internal static class Program
{
    private static readonly object OutputGate = new();
    private static readonly List<PosixSignalRegistration> Signals = [];

    private static int Main(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        try
        {
            return command switch
            {
                "run" => Run(rest),
                "devices" => Devices(rest),
                "refresh-rate" => RefreshRate(),
                "bench" => Bench.Run(Int(Value(rest, "--functions")) ?? 50, Int(Value(rest, "--rate")) ?? Native.HighestRefreshRateHz(),
                    Int(Value(rest, "--seconds")) ?? 10, Int(Value(rest, "--sources")) ?? 4, Emit),
                _ => Help()
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("JoyPro.VJoy run --config <file> [--sink vjoy|dry] [--xbox-sink vigem|dry] [--rate Hz] [--parent-pid N] [--protocol] [--monitor] [--duration seconds] [--vjoy-dll path]");
        Console.WriteLine("JoyPro.VJoy devices [--json]");
        Console.WriteLine("JoyPro.VJoy refresh-rate");
        Console.WriteLine("JoyPro.VJoy bench [--functions 50] [--rate Hz] [--seconds 10] [--sources 4]   (the loop with virtual functions, no devices)");
        return 1;
    }

    private static int RefreshRate()
    {
        Console.WriteLine(Native.HighestRefreshRateHz().ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static void Emit(string line)
    {
        lock (OutputGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static int Run(List<string> args)
    {
        var configPath = Value(args, "--config") ?? throw new ArgumentException("run needs --config <file> (engine vjoy forwarder-config --out <file>).");
        var sinkName = (Value(args, "--sink") ?? "dry").ToLowerInvariant();
        var xboxSinkName = (Value(args, "--xbox-sink") ?? "dry").ToLowerInvariant();
        if (Environment.GetEnvironmentVariable("JOYPRO_VJOY_DRY") == "1")
        {
            sinkName = "dry";
            xboxSinkName = "dry";
        }
        var rate = Int(Value(args, "--rate")) ?? Native.HighestRefreshRateHz();
        var parentPid = Int(Value(args, "--parent-pid")) ?? 0;
        var duration = Int(Value(args, "--duration"));
        var protocol = Flag(args, "--protocol");
        var monitor = Flag(args, "--monitor");
        var dll = Value(args, "--vjoy-dll");
        var mode = (Value(args, "--mode") ?? "event").ToLowerInvariant();
        if (mode is not ("event" or "poll"))
            throw new ArgumentException("--mode takes event or poll.");
        if (args.Count > 0)
            throw new ArgumentException("Unknown argument(s): " + string.Join(" ", args));

        ForwarderConfig config;
        try
        {
            config = ForwarderConfig.Parse(File.ReadAllText(configPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Cannot read the forwarding table " + configPath + ": " + ex.Message);
            return 1;
        }

        if ((config.Slots.Count == 0 || config.VJoyDevices.Count == 0) && config.Xbox.Count == 0 && config.Functions.Count == 0)
        {
            Console.Error.WriteLine("The forwarding table is empty: no game uses virtual mode, or none of its relations has a bind on a game controller.");
            return 1;
        }

        IXboxSink? xboxSink = null;
        if (config.UsesXbox)
        {
            xboxSink = xboxSinkName switch
            {
                "vigem" => new ViGEmXboxSink(),
                "dry" => new DryXboxSink(),
                _ => throw new ArgumentException("--xbox-sink takes vigem or dry.")
            };
            if (!xboxSink.Open(out var xboxError))
            {
                Emit("error " + xboxError);
                xboxSink.Dispose();
                return 2;
            }
        }

        IVJoySink sink;
        if (config.VJoyDevices.Count == 0)
        {
            sink = new DrySink(); // only the Xbox pad
        }
        else if (sinkName == "vjoy")
        {
            VJoyNative.Configure(dll);
            sink = new VJoyInterfaceSink();
        }
        else if (sinkName == "dry")
        {
            sink = new DrySink();
        }
        else
        {
            throw new ArgumentException("--sink takes vjoy or dry.");
        }

        if (!sink.Open(config.VJoyDevices, out var error))
        {
            Emit("error " + error);
            sink.Dispose();
            xboxSink?.Dispose();
            return 2;
        }

        // Keys (virtual functions with "Keyboard output" on) go out only with a real sink; a dry run never types.
        var realKeys = (sink.Name == "vjoy" || xboxSink?.Name == "vigem") && Environment.Is64BitProcess &&
                       config.Functions.Any(f => f.Enabled && f.Keyboard);
        IVfKeySink keys = realKeys ? new SendInputKeySink() : new DryKeySink();
        using var forwarder = new Forwarder(config, sink, rate, parentPid, Emit, xboxSink, keys) { PollOnly = mode == "poll" };
        var exit = new ManualResetEventSlim();
        try
        {
            forwarder.Start();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Emit("error " + ex.Message);
            sink.Dispose();
            return 2;
        }

        Emit("ready sink=" + sink.Name + " rate=" + forwarder.RateHz.ToString(CultureInfo.InvariantCulture) + " period-ms=" + forwarder.PeriodMs.ToString(CultureInfo.InvariantCulture) +
             " devices=" + string.Join(",", config.VJoyDevices) + " sources=" + forwarder.ConnectedSources.ToString(CultureInfo.InvariantCulture) + "/" + forwarder.SourceCount.ToString(CultureInfo.InvariantCulture) +
             " slots=" + forwarder.Table.SlotCount.ToString(CultureInfo.InvariantCulture) +
             " functions=" + (forwarder.Functions?.Functions.Length ?? 0).ToString(CultureInfo.InvariantCulture) + (realKeys ? " keys=on" : string.Empty) +
             (forwarder.Xbox is null ? "" : " xbox=" + (xboxSink?.Name ?? "") + ":" + forwarder.Xbox.SlotCount.ToString(CultureInfo.InvariantCulture) + (config.XboxGame.Length > 0 ? ":" + config.XboxGame : "")) + " axis-range=" + sink.AxisMin.ToString(CultureInfo.InvariantCulture) + ".." + sink.AxisMax.ToString(CultureInfo.InvariantCulture));

        var monitorOn = monitor ? 1 : 0;
        void Stop(string reason)
        {
            forwarder.Stop(reason);
            exit.Set();
        }

        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            Signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                Stop("signal " + signal);
            }));
        }

        if (protocol)
        {
            new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        switch (line.Trim().ToLowerInvariant())
                        {
                            case "stop":
                                Stop("stop");
                                return;
                            case "monitor on":
                                Interlocked.Exchange(ref monitorOn, 1);
                                break;
                            case "monitor off":
                                Interlocked.Exchange(ref monitorOn, 0);
                                break;
                        }
                    }
                }
                catch (IOException)
                {
                }

                Stop("stdin closed");
            }) { IsBackground = true, Name = "JoyPro vJoy stdin" }.Start();
        }

        // Reporting (not the loop): stats once a second, the live view at ~15 Hz while it is on.
        var positions = new VJoyPosition[forwarder.Table.DeviceIds.Length];
        var builder = new StringBuilder(160);
        var process = Process.GetCurrentProcess();
        var started = Stopwatch.StartNew();
        var lastStats = 0L;
        var lastPasses = 0L;
        while (!exit.Wait(66))
        {
            while (forwarder.TryTakeEvent(out var message))
                Emit(message);
            if (Volatile.Read(ref monitorOn) == 1)
            {
                forwarder.CopyPositions(positions);
                for (var i = 0; i < positions.Length; i++)
                    Emit(Forwarder.Describe(ref positions[i], builder));
                if (forwarder.CopyXbox(out var xboxReport))
                    Emit(Forwarder.DescribeXbox(ref xboxReport, builder));
                // "fn <id> <input 0..1000> <state> <active outputs mask>" per virtual function.
                if (forwarder.Functions is { } functions)
                {
                    foreach (var function in functions.Functions)
                        Emit(DescribeFunction(function, builder));
                }
            }

            if (forwarder.Wait(0))
                break; // the loop ended by itself (parent exited)
            if (started.ElapsedMilliseconds - lastStats >= 1000)
            {
                var passes = forwarder.Passes;
                var seconds = (started.ElapsedMilliseconds - lastStats) / 1000.0;
                lastStats = started.ElapsedMilliseconds;
                Emit("stats passes=" + passes.ToString(CultureInfo.InvariantCulture) +
                     " hz=" + ((passes - lastPasses) / seconds).ToString("0", CultureInfo.InvariantCulture) +
                     " wakes=" + forwarder.Wakes.ToString(CultureInfo.InvariantCulture) +
                     " changes=" + forwarder.Changes.ToString(CultureInfo.InvariantCulture) +
                     " writes=" + forwarder.Writes.ToString(CultureInfo.InvariantCulture) +
                     " sources=" + forwarder.ConnectedSources.ToString(CultureInfo.InvariantCulture) + "/" + forwarder.SourceCount.ToString(CultureInfo.InvariantCulture) +
                     " loop-alloc=" + forwarder.LoopAllocatedBytes.ToString(CultureInfo.InvariantCulture) +
                     " cpu-ms=" + process.TotalProcessorTime.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) +
                     " ws-kb=" + (Environment.WorkingSet / 1024).ToString(CultureInfo.InvariantCulture) +
                     " handles=" + HandleCount().ToString(CultureInfo.InvariantCulture));
                lastPasses = passes;
            }

            if (duration is int limit && started.Elapsed.TotalSeconds >= limit)
                Stop("duration");
        }

        forwarder.Wait(3000);
        var reason = forwarder.ExitReason;
        forwarder.Dispose();
        sink.Dispose();
        xboxSink?.Dispose();
        Emit("exit " + reason);
        return 0;
    }

    internal static string DescribeFunction(VfFunction function, StringBuilder sb)
    {
        sb.Clear();
        sb.Append("fn ").Append(function.Id).Append(' ')
            .Append(((int)Math.Round(function.Input * 1000)).ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(function.State.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(function.Active.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static int Devices(List<string> args)
    {
        var json = Flag(args, "--json");
        var list = new List<(Guid Instance, Guid Product, string Name, string Path)>();
        Native.IDirectInput8W? directInput = null;
        try
        {
            var iid = Native.IidIDirectInput8W;
            if (Native.DirectInput8Create(Native.GetModuleHandle(null), Native.DirectInputVersion, ref iid, out directInput, IntPtr.Zero) < 0 || directInput is null)
                throw new COMException("DirectInput8Create failed");
            var instances = new List<Native.DideviceInstanceW>();
            var handle = GCHandle.Alloc(instances);
            Native.EnumCallback callback = (item, state) =>
            {
                if (GCHandle.FromIntPtr(state).Target is List<Native.DideviceInstanceW> target)
                    target.Add(Marshal.PtrToStructure<Native.DideviceInstanceW>(item));
                return 1;
            };
            try
            {
                directInput.EnumDevices(Native.Di8DevClassGameCtrl, Marshal.GetFunctionPointerForDelegate(callback), GCHandle.ToIntPtr(handle), Native.DiedflAttachedOnly);
            }
            finally
            {
                handle.Free();
                GC.KeepAlive(callback);
            }

            foreach (var instance in instances)
            {
                var path = "";
                var guid = instance.guidInstance;
                if (directInput.CreateDevice(ref guid, out var device, IntPtr.Zero) >= 0 && device is not null)
                {
                    const int size = 16 + 16 + 520;
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        for (var i = 0; i < size; i++)
                            Marshal.WriteByte(buffer, i, 0);
                        Marshal.WriteInt32(buffer, 0, size);
                        Marshal.WriteInt32(buffer, 4, 16);
                        if (device.GetProperty(new IntPtr(Native.DiPropGuidAndPath), buffer) >= 0)
                            path = Marshal.PtrToStringUni(buffer + 32) ?? "";
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                        Marshal.ReleaseComObject(device);
                    }
                }

                list.Add((instance.guidInstance, instance.guidProduct, instance.tszInstanceName, path));
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException)
        {
            Console.Error.WriteLine("DirectInput: " + ex.Message);
            return 2;
        }
        finally
        {
            if (directInput is not null)
                Marshal.ReleaseComObject(directInput);
        }

        if (json)
        {
            var array = new JsonArray();
            foreach (var d in list)
                array.Add(new JsonObject { ["name"] = d.Name, ["instanceGuid"] = d.Instance.ToString("D"), ["productGuid"] = d.Product.ToString("D"), ["hidPath"] = d.Path });
            Console.WriteLine(new JsonObject { ["devices"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("Game controllers: " + list.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var d in list)
            Console.WriteLine("  " + d.Name + " {" + d.Instance.ToString("D").ToUpperInvariant() + "} " + d.Path);
        return 0;
    }

    private static string? Value(List<string> args, string name)
    {
        var index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (index + 1 >= args.Count)
            throw new ArgumentException("Missing value for " + name);
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }

    private static bool Flag(List<string> args, string name)
    {
        var index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        args.RemoveAt(index);
        return true;
    }

    // GetProcessHandleCount: Process.HandleCount would snapshot every process of the PC each time.
    private static uint HandleCount() => GetProcessHandleCount(GetCurrentProcess(), out var count) ? count : 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    private static int? Int(string? value) =>
        value is null ? null : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new ArgumentException("Not a number: " + value);
}
