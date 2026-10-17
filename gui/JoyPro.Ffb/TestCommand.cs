using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Input;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb;

/// <summary>JoyPro.Ffb test: the GUI's direction test drives the stick through this (one command per stdin line).</summary>
/// <remarks>
/// Commands (answers are JSON lines on stdout: {"type":"ok"}, {"type":"position","x":..,"y":..},
/// {"type":"error","message":..}):
///   invert X Y          0/1 each: the inverts the next forces use
///   push X|Y magnitude  a constant force (JoyPro's convention), clamped to +-25 %
///   spring k cx cy      a centring spring on both axes, stiffness k (max 1), saturation 25 %, centre (cx, cy)
///   conditions combined|separate|restart   how springs go to the device (hardware A/B test; default combined)
///   pos                 the stick position (read non-exclusively)
///   stop                every force off, the device released
///   quit                stop and exit (also on end of stdin, on --parent-pid exit)
/// Safety: forces are clamped to 25 %, a push ends by itself after <see cref="PushLimitSeconds"/> and a spring after
/// <see cref="SpringLimitSeconds"/> without a new command; every exit path releases the device. JOYPRO_FFB_DRY=1 or
/// --sink dry uses the dry sink (no hardware; position answers "valid": false).
/// </remarks>
internal static class TestCommand
{
    public const double MaxForce = 0.25;
    public const double PushLimitSeconds = 4;
    public const double SpringLimitSeconds = 30;

    private static readonly object Gate = new();
    private static List<FfbEffectCommand> _commands = [];
    private static double _deadline;
    private static bool _active;

    public static int Run(IFfbBackend backend, IFfbInputs? inputs, int? parentPid, Action<string> emit)
    {
        var clock = Stopwatch.StartNew();
        using var exit = new ManualResetEventSlim();
        // Every way out releases the device: end of stdin and "quit" below, the GUI exiting (the blocking stdin read
        // would never return: release and end the process), a closing console or Ctrl+C.
        void ReleaseNow()
        {
            lock (Gate)
            {
                _commands = [];
                _active = false;
                Release(backend);
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseNow();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => ReleaseNow());
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => ReleaseNow());
        if (parentPid is { } pid)
        {
            FollowParent(pid, () =>
            {
                exit.Set();
                ReleaseNow();
                Environment.Exit(0);
            });
        }

        var device = backend.Devices.Count > 0 ? backend.Devices[0].DeviceId : "dry";
        var loop = new Thread(() => Loop(backend, clock, exit)) { IsBackground = true, Name = "JoyPro FFB test loop" };
        loop.Start();
        try
        {
            string? line;
            while (!exit.IsSet && (line = Console.In.ReadLine()) is not null)
            {
                // Every command and its answer go to the log (the direction test is short; nothing here repeats per tick).
                JoyPro.Logging.JoyProLog.Info("FfbTest", "Command: " + line.Trim().TrimStart('﻿'));
                var reply = Handle(line, backend, inputs, device, clock.Elapsed.TotalSeconds, out var quit);
                JoyPro.Logging.JoyProLog.Info("FfbTest", "Answer: " + reply);
                emit(reply);
                if (quit)
                    break;
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            exit.Set();
            loop.Join(TimeSpan.FromSeconds(2));
            lock (Gate)
            {
                _commands = [];
                _active = false;
                Release(backend);
            }
        }

        return 0;
    }

    /// <summary>One command line -> one answer line. Public for the tests of the parser (no hardware).</summary>
    internal static string Handle(string line, IFfbBackend backend, IFfbInputs? inputs, string device, double now, out bool quit)
    {
        quit = false;
        line = line.Trim().TrimStart('﻿'); // a byte order mark from a shell pipe
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Error("empty command");
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "invert" when parts.Length == 3:
                    lock (Gate)
                    {
                        if (backend is DirectInputFfbBackend di)
                        {
                            // New directions only on a fresh start of the effects: stop and release first.
                            _commands = [];
                            _active = false;
                            Release(backend);
                            di.InvertX = parts[1] == "1";
                            di.InvertY = parts[2] == "1";
                        }
                    }

                    return Ok();
                case "push" when parts.Length == 3:
                {
                    var axis = parts[1].Equals("X", StringComparison.OrdinalIgnoreCase) ? FfbAxis.X : FfbAxis.Y;
                    var magnitude = Math.Clamp(Number(parts[2]), -MaxForce, MaxForce);
                    Set(backend, [new FfbEffectCommand(device, axis, FfbEffectType.ConstantForce, Magnitude: magnitude, Tag: "test")], now, now + PushLimitSeconds);
                    return Ok();
                }
                case "spring" when parts.Length == 4:
                {
                    var k = Math.Clamp(Number(parts[1]), 0, 1);
                    var cx = Math.Clamp(Number(parts[2]), -1, 1);
                    var cy = Math.Clamp(Number(parts[3]), -1, 1);
                    Set(backend,
                    [
                        new FfbEffectCommand(device, FfbAxis.X, FfbEffectType.Spring, Centre: cx, Coefficient: k, Saturation: MaxForce, Tag: "test"),
                        new FfbEffectCommand(device, FfbAxis.Y, FfbEffectType.Spring, Centre: cy, Coefficient: k, Saturation: MaxForce, Tag: "test")
                    ], now, now + SpringLimitSeconds);
                    return Ok();
                }
                case "conditions" when parts.Length == 2:
                {
                    var layout = parts[1].ToLowerInvariant() switch
                    {
                        "combined" => FfbConditionLayout.Combined,
                        "separate" => FfbConditionLayout.Separate,
                        "restart" => FfbConditionLayout.SeparateRestart,
                        _ => throw new FormatException("conditions combined|separate|restart")
                    };
                    lock (Gate)
                    {
                        if (backend is DirectInputFfbBackend di)
                        {
                            // A new layout only on a fresh start of the effects: stop and release first.
                            _commands = [];
                            _active = false;
                            Release(backend);
                            di.ConditionLayout = layout;
                        }
                    }

                    return Ok();
                }
                case "pos":
                {
                    if (inputs is not null)
                    {
                        inputs.Poll();
                        if (inputs.TryGetStick("", FfbAxis.X, out var x) && inputs.TryGetStick("", FfbAxis.Y, out var y))
                            return new JsonObject { ["type"] = "position", ["valid"] = true, ["x"] = Math.Round(x, 4), ["y"] = Math.Round(y, 4) }.ToJsonString();
                    }

                    return new JsonObject { ["type"] = "position", ["valid"] = false }.ToJsonString();
                }
                case "stop":
                    lock (Gate)
                    {
                        _commands = [];
                        _active = false;
                        Release(backend);
                    }

                    return Ok();
                case "quit":
                    quit = true;
                    return Ok();
                default:
                    return Error("unknown command: " + line.Trim());
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Error(ex.Message);
        }
    }

    // Sends the commands at once (so an error, e.g. the game holding the device, is the answer) and keeps them up.
    private static void Set(IFfbBackend backend, List<FfbEffectCommand> commands, double now, double deadline)
    {
        lock (Gate)
        {
            _commands = commands;
            _deadline = deadline;
            _active = true;
            try
            {
                backend.Apply(now, commands.Select(c => c with { DurationSeconds = 0.3 }).ToList());
            }
            catch (Exception)
            {
                _commands = [];
                _active = false;
                Release(backend);
                throw;
            }
        }
    }

    // 50 Hz: the current commands with a 0.3 s duration (the condition effects run infinite on the device anyway);
    // past the deadline everything stops and the device is released.
    private static void Loop(IFfbBackend backend, Stopwatch clock, ManualResetEventSlim exit)
    {
        while (!exit.Wait(20))
        {
            lock (Gate)
            {
                if (!_active)
                    continue;
                var now = clock.Elapsed.TotalSeconds;
                if (now > _deadline)
                {
                    _commands = [];
                    _active = false;
                    Release(backend);
                    continue;
                }

                try
                {
                    backend.Apply(now, _commands.Select(c => c with { DurationSeconds = 0.3 }).ToList());
                }
                catch (Exception)
                {
                    _commands = [];
                    _active = false;
                    Release(backend);
                }
            }
        }
    }

    private static void Release(IFfbBackend backend)
    {
        try
        {
            backend.StopAll();
        }
        catch (Exception)
        {
        }
    }

    private static void FollowParent(int pid, Action gone)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => gone();
            if (parent.HasExited)
                gone();
        }
        catch (ArgumentException)
        {
            gone();
        }
    }

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new FormatException("not a number: " + text);

    private static string Ok() => "{\"type\":\"ok\"}";

    private static string Error(string message) => new JsonObject { ["type"] = "error", ["message"] = message }.ToJsonString();
}
