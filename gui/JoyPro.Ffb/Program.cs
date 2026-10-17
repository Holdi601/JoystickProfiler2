using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Logging;

namespace JoyPro.Ffb;

/// <summary>JoyPro's telemetry force feedback process (docs/ffb-telemetry.md).</summary>
/// <remarks>
/// JoyPro.Ffb devices [--json]
///     force feedback devices, their effects and force axes (read-only: nothing is acquired or sent)
/// JoyPro.Ffb run --game DCS|IL2Game|IL2Korea|WarThunder [--sink directinput|dry] [--record file.jsonl]
///                [--game-pid N] [--parent-pid N] [--protocol] [--config file] [--no-game-check]
///                [--port N] [--wt-url URL] [--duration seconds] [--no-status-file]
///     runs until the game exits, the parent (GUI) exits, stdin ends (--protocol), "stop", Ctrl+C, or --duration
/// JoyPro.Ffb preset [--out file]
///     the built-in flight graph as JSON
/// JoyPro.Ffb simulate --game G --port N [--seconds S] [--rate Hz] [--gap-from S --gap-to S]
///     plays the synthetic test flight like the game would send it (tests)
/// JOYPRO_FFB_DRY=1 forces the dry sink (the smoke tests set it).
/// </remarks>
internal static class Program
{
    private static readonly object OutputGate = new();
    private static readonly ManualResetEventSlim ExitSignal = new();
    private static string _exitReason = "stopped";
    private static TelemetryFfbHost? _host;
    private static IFfbBackend? _backend;
    private static DirectInputInputs? _inputs;
    private static int _cleanedUp;
    private static readonly List<PosixSignalRegistration> Signals = [];

    private static int Main(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        // Background log (docs/logging.md): <data>\Logs\ffb-<date>.log, with the GUI's session id when it started us.
        // Device and state changes only; the force loop itself is never logged per tick (a summary every minute).
        JoyProLog.Start("ffb", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?", args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => JoyProLog.Error("Crash", "Unhandled exception (terminating: " + e.IsTerminating + ")", e.ExceptionObject as Exception);
        var exitCode = 1;
        try
        {
            var options = Options.Parse(args.Skip(1));
            exitCode = command switch
            {
                "devices" => Devices(options),
                "run" => Run(options),
                "preset" => Preset(options),
                "simulate" => Simulate(options),
                "test" => Test(options),
                _ => Help()
            };
            return exitCode;
        }
        catch (ArgumentException ex)
        {
            JoyProLog.Warn("Ffb", "Invalid arguments: " + ex.Message);
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
            return 1;
        }
        finally
        {
            JoyProLog.Info("Ffb", "JoyPro.Ffb " + command + " finished: exit code " + exitCode.ToString(CultureInfo.InvariantCulture) +
                                  (command == "run" ? " (" + _exitReason + ")" : string.Empty));
            JoyProLog.Flush();
        }
    }

    private static int Help()
    {
        Console.WriteLine("JoyPro.Ffb devices [--json]");
        Console.WriteLine("JoyPro.Ffb run --game DCS|IL2Game|IL2Korea|WarThunder [--sink directinput|dry] [--record file.jsonl] [--game-pid N] [--parent-pid N] [--protocol] [--config file] [--no-game-check] [--port N] [--wt-url URL] [--duration seconds] [--no-status-file] [--conditions combined|separate|restart]");
        Console.WriteLine("JoyPro.Ffb preset [--out file]");
        Console.WriteLine("JoyPro.Ffb simulate --game DCS|IL2Game|IL2Korea|WarThunder --port N [--seconds S] [--rate Hz] [--gap-from S --gap-to S]");
        Console.WriteLine("JoyPro.Ffb test [--device GUID] [--config file] [--sink directinput|dry] [--parent-pid N]   (stdin: invert X Y | push X|Y m | spring k cx cy | conditions combined|separate|restart | pos | stop | quit)");
        return 1;
    }

    // The direction test's process: a device of its own, commands on stdin (TestCommand).
    private static int Test(Options options)
    {
        var config = TelemetryFfbConfig.Load(options.Value("--config") ?? TelemetryFfbPaths.ConfigFile());
        var sink = (options.Value("--sink") ?? "directinput").ToLowerInvariant();
        if (Environment.GetEnvironmentVariable("JOYPRO_FFB_DRY") == "1")
            sink = "dry";
        void Say(string line) => Emit(true, line);
        if (sink == "dry")
        {
            using var dry = new DryFfbBackend(options.Value("--record"));
            Say(HostProtocol.LogLine("test: dry sink"));
            return TestCommand.Run(dry, null, ParseInt(options.Value("--parent-pid")), Say);
        }

        if (!Environment.Is64BitProcess)
            throw new ArgumentException("The DirectInput sink needs a 64-bit process.");
        var warnings = new List<string>();
        var device = DirectInputFfb.Find(options.Value("--device") ?? config.Device, warnings);
        if (device is null)
        {
            Say(new JsonObject { ["type"] = "error", ["message"] = "No force feedback device found" + (warnings.Count > 0 ? ": " + string.Join("; ", warnings) : ".") }.ToJsonString());
            return 2;
        }

        using var backend = new DirectInputFfbBackend(device, false, false) { Log = message => JoyProLog.Info("FfbTest", message) };
        using var inputs = new DirectInputInputs(device.InstanceGuid);
        Say(HostProtocol.LogLine("test: " + device.Name));
        return TestCommand.Run(backend, inputs, ParseInt(options.Value("--parent-pid")), Say);
    }

    private static int Devices(Options options)
    {
        var warnings = new List<string>();
        var devices = DirectInputFfb.Enumerate(warnings);
        if (options.Has("--json"))
        {
            var array = new JsonArray();
            foreach (var d in devices)
            {
                array.Add(new JsonObject
                {
                    ["instanceGuid"] = d.InstanceToken,
                    ["productGuid"] = "{" + d.ProductGuid.ToString().ToUpperInvariant() + "}",
                    ["name"] = d.Name,
                    ["productName"] = d.ProductName,
                    ["vid"] = d.VendorId.ToString("X4", CultureInfo.InvariantCulture),
                    ["pid"] = d.ProductId.ToString("X4", CultureInfo.InvariantCulture),
                    ["forceAxes"] = new JsonArray(d.ForceAxes.Select(a => (JsonNode?)a).ToArray()),
                    ["effects"] = new JsonArray(d.Effects.Select(e => (JsonNode?)e).ToArray())
                });
            }

            var root = new JsonObject { ["devices"] = array, ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray()) };
            Console.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("Force feedback devices: " + devices.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var d in devices)
        {
            Console.WriteLine($"  {d.Name} {d.InstanceToken} (VID {d.VendorId:X4}, PID {d.ProductId:X4})");
            Console.WriteLine("    force axes: " + (d.ForceAxes.Count == 0 ? "-" : string.Join(", ", d.ForceAxes)));
            Console.WriteLine("    effects: " + (d.Effects.Count == 0 ? "-" : string.Join(", ", d.Effects)));
        }

        foreach (var warning in warnings)
            Console.WriteLine("  warning: " + warning);
        return 0;
    }

    private static int Preset(Options options)
    {
        var json = FfbGraphPersistence.ToJson(FfbPresets.Flight());
        var path = options.Value("--out");
        if (path is null)
            Console.WriteLine(json);
        else
            File.WriteAllText(path, json);
        return 0;
    }

    private static int Simulate(Options options)
    {
        var game = TelemetryGame.Find(options.Value("--game") ?? "")
                   ?? throw new ArgumentException("simulate needs --game " + string.Join("|", TelemetryGame.All.Select(g => g.Id)) + ".");
        var port = ParseInt(options.Value("--port")) ?? throw new ArgumentException("simulate needs --port (the game's default ports are left alone).");
        double Seconds(string name, double fallback) =>
            double.TryParse(options.Value(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        return Simulator.Run(game, port, Seconds("--seconds", 30), Seconds("--rate", game.Id == TelemetryGame.Dcs ? 60 : 50),
            Seconds("--gap-from", double.PositiveInfinity), Seconds("--gap-to", double.PositiveInfinity));
    }

    private static int Run(Options options)
    {
        var game = TelemetryGame.Find(options.Value("--game") ?? "")
                   ?? throw new ArgumentException("run needs --game " + string.Join("|", TelemetryGame.All.Select(g => g.Id)) + ".");
        var configPath = options.Value("--config") ?? TelemetryFfbPaths.ConfigFile();
        var config = TelemetryFfbConfig.Load(configPath);
        var protocol = options.Has("--protocol");
        var clock = new StopwatchClock();
        var sink = (options.Value("--sink") ?? "directinput").ToLowerInvariant();
        if (Environment.GetEnvironmentVariable("JOYPRO_FFB_DRY") == "1")
            sink = "dry";

        JoyProLog.Info("Ffb", "Run: game " + game.Id + ", sink " + sink + ", config " + configPath + " (enabled " + config.Enabled + ", device '" + config.Device + "')" +
                              (protocol ? ", GUI protocol" : string.Empty));
        string deviceName;
        if (sink == "dry")
        {
            _backend = new DryFfbBackend(options.Value("--record"), clock);
            deviceName = "dry";
        }
        else if (sink == "directinput")
        {
            if (!Environment.Is64BitProcess)
                throw new ArgumentException("The DirectInput sink needs a 64-bit process.");
            var warnings = new List<string>();
            var device = DirectInputFfb.Find(config.Device, warnings);
            if (device is null)
            {
                JoyProLog.Warn("Ffb", "No force feedback device found" + (config.Device.Length > 0 ? " for " + config.Device : "") +
                                      (warnings.Count > 0 ? ": " + string.Join("; ", warnings) : "."));
                Emit(protocol, HostProtocol.LogLine("No force feedback device found" + (config.Device.Length > 0 ? " for " + config.Device : "") +
                                                    (warnings.Count > 0 ? ": " + string.Join("; ", warnings) : ".")));
                Emit(protocol, HostProtocol.ExitLine("no device"));
                return 2;
            }

            var (invertX, invertY) = config.InvertFor(device.InstanceToken);
            JoyProLog.Info("Ffb", "Device: " + device.Name + " " + device.InstanceToken + " (VID " + device.VendorId.ToString("X4", CultureInfo.InvariantCulture) +
                                  ", PID " + device.ProductId.ToString("X4", CultureInfo.InvariantCulture) + "), force axes " + string.Join(",", device.ForceAxes) +
                                  ", effects " + string.Join(",", device.Effects) + ", invert x/y " + invertX + "/" + invertY +
                                  (warnings.Count > 0 ? ", warnings: " + string.Join("; ", warnings) : string.Empty));
            _backend = new DirectInputFfbBackend(device, invertX, invertY)
            {
                // Device calls: the first ones (acquire, effects created) in full, then at most one line a second per kind.
                Log = message => { if (JoyProLog.ShouldLog("ffb-device:" + message.Split(' ')[0], TimeSpan.FromSeconds(1))) JoyProLog.Debug("Ffb", message); }
            };
            // Hardware A/B test of the spring layout (docs/ffb-telemetry.md): --conditions combined|separate|restart.
            if (options.Value("--conditions") is { } layout)
            {
                ((DirectInputFfbBackend)_backend).ConditionLayout = layout.ToLowerInvariant() switch
                {
                    "separate" => FfbConditionLayout.Separate,
                    "restart" => FfbConditionLayout.SeparateRestart,
                    _ => FfbConditionLayout.Combined
                };
            }

            _inputs = new DirectInputInputs(device.InstanceGuid);
            deviceName = device.Name;
        }
        else
        {
            throw new ArgumentException("--sink directinput|dry");
        }

        var portText = options.Value("--port");
        var hostOptions = new TelemetryFfbHostOptions
        {
            GamePid = ParseInt(options.Value("--game-pid")),
            IgnoreGameProcess = options.Has("--no-game-check"),
            PortOverride = portText is null ? null : ParseInt(portText),
            WarThunderUri = options.Value("--wt-url") is { } url ? new Uri(url) : null,
            Inputs = _inputs
        };
        var statusFile = options.Has("--no-status-file") ? null : TelemetryFfbPaths.StatusFile();
        _host = new TelemetryFfbHost(config, game, _backend, clock, hostOptions);
        _host.StatusChanged += status =>
        {
            Emit(protocol, protocol ? HostProtocol.StatusLine(status, deviceName) : status.Reason);
            WriteStatusFile(statusFile, status, deviceName);
        };
        _host.GameExited += () => RequestExit("game exited");

        InstallExitHandlers();
        if (ParseInt(options.Value("--parent-pid")) is { } parent)
            FollowParent(parent);
        _host.Start();
        if (protocol)
            StartCommandReader(configPath);

        Timer? watchTimer = null;
        watchTimer = new Timer(_ => SendWatchLines(protocol), null, Timeout.Infinite, Timeout.Infinite);
        WatchTimer = watchTimer;
        using var diagTimer = options.Has("--diag")
            ? new Timer(_ => Emit(protocol, Diagnostics()), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))
            : null;
        // Rate-limited summary for the log: once a minute (frames, updates, memory), never per tick.
        using var summaryTimer = JoyProLog.IsStarted
            ? new Timer(_ => JoyProLog.Debug("Ffb", "Summary: " + (_host?.Status is { } s ? s.StateName + ", " + s.Frames.ToString(CultureInfo.InvariantCulture) + " frames, " : string.Empty) +
                                                    Diagnostics()), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))
            : null;

        var duration = options.Value("--duration") is { } d && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : (double?)null;
        if (duration is { } limit)
            ExitSignal.Wait(TimeSpan.FromSeconds(limit));
        else
            ExitSignal.Wait();
        if (!ExitSignal.IsSet)
            _exitReason = "duration over";

        watchTimer.Dispose();
        var faulted = _host.Runtime?.State == Core.Runtime.FfbRuntimeState.Faulted;
        Cleanup();
        WriteExitStatus(statusFile, game);
        Emit(protocol, protocol ? HostProtocol.ExitLine(_exitReason) : "exit: " + _exitReason);
        if (_backend is DryFfbBackend dry && !protocol)
            Console.WriteLine($"dry sink: {dry.ApplyCount} updates, {dry.StopAllCount} stop-all");
        return faulted ? 3 : 0;
    }

    private static Timer? WatchTimer;

    // --diag: memory and GC numbers every 10 s (the resource test reads them to tell a leak from the GC's budget).
    private static string Diagnostics()
    {
        using var self = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        var text = string.Create(CultureInfo.InvariantCulture,
            $"diag: managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, heap after last GC {info.HeapSizeBytes / 1048576.0:0.0} MB, " +
            $"gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, private {self.PrivateMemorySize64 / 1048576.0:0.0} MB, " +
            $"working set {self.WorkingSet64 / 1048576.0:0.0} MB, handles {self.HandleCount}, threads {self.Threads.Count}, " +
            $"cpu {self.TotalProcessorTime.TotalSeconds:0.00} s, updates {(_host?.Runtime?.Evaluations ?? 0)}");
        return HostProtocol.LogLine(text);
    }

    // ------------------------------------------------------------------ the GUI's commands (stdin)

    private static void StartCommandReader(string configPath)
    {
        var thread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                {
                    JoyProLog.Debug("Ffb", "GUI command: " + line.Trim());
                    switch (line.Trim().ToLowerInvariant())
                    {
                        case HostProtocol.WatchOn:
                            _host?.Watch(true);
                            WatchTimer?.Change(0, 200);
                            break;
                        case HostProtocol.WatchOff:
                            _host?.Watch(false);
                            WatchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                            break;
                        case HostProtocol.Reload:
                            _host?.Reload(TelemetryFfbConfig.Load(configPath));
                            break;
                        case HostProtocol.Stop:
                            RequestExit("stop requested");
                            return;
                    }
                }

                RequestExit("the GUI closed the connection");
            }
            catch (IOException)
            {
                RequestExit("the GUI connection broke");
            }
        })
        {
            IsBackground = true,
            Name = "JoyPro FFB commands"
        };
        thread.Start();
    }

    private static void SendWatchLines(bool protocol)
    {
        var host = _host;
        if (host is null || !host.Watching || !protocol)
            return;
        var frame = host.Source?.Latest;
        if (frame is not null)
            Emit(true, HostProtocol.ValuesLine("telemetry", frame.Values, frame.Time));
        var nodes = host.Runtime?.NodeValues;
        if (nodes is { Count: > 0 })
            Emit(true, HostProtocol.ValuesLine("nodes", nodes));
    }

    // ------------------------------------------------------------------ ending safely

    private static void InstallExitHandlers()
    {
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            Signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = signal is PosixSignal.SIGINT or PosixSignal.SIGQUIT;
                RequestExit("signal " + signal);
                // A closing console or a shutdown gives a few seconds only: silence the device right here.
                if (!context.Cancel)
                    Cleanup();
            }));
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Cleanup();
    }

    private static void FollowParent(int pid)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => RequestExit("JoyPro exited");
            if (parent.HasExited)
                RequestExit("JoyPro exited");
        }
        catch (ArgumentException)
        {
            RequestExit("JoyPro is not running");
        }
    }

    private static void RequestExit(string reason)
    {
        if (!ExitSignal.IsSet)
        {
            _exitReason = reason;
            JoyProLog.Info("Ffb", "Exit requested: " + reason);
        }
        ExitSignal.Set();
    }

    /// <summary>STOP ALL and release the device; once, from whichever path ends the process first.</summary>
    private static void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
            return;
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            JoyProLog.Warn("Ffb", "Stopping the host failed", ex);
        }

        try
        {
            _backend?.StopAll();
            _backend?.Dispose();
            JoyProLog.Debug("Ffb", "STOP ALL sent, device released");
        }
        catch (Exception ex)
        {
            JoyProLog.Warn("Ffb", "STOP ALL / releasing the device failed", ex);
        }

        try
        {
            _inputs?.Dispose();
        }
        catch (Exception)
        {
        }
        JoyProLog.Flush();
    }

    private static void Emit(bool protocol, string line)
    {
        lock (OutputGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static void WriteStatusFile(string? path, TelemetryFfbStatus status, string device)
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["game"] = status.Game,
                ["state"] = status.StateName,
                ["reason"] = status.Reason,
                ["aircraft"] = status.Aircraft,
                ["graph"] = status.Graph,
                ["frames"] = status.Frames,
                ["endpoint"] = status.Endpoint,
                ["device"] = device,
                ["updated"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteExitStatus(string? path, TelemetryGame game)
    {
        if (path is null)
            return;
        try
        {
            var json = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["game"] = game.Id,
                ["state"] = "exited",
                ["reason"] = _exitReason,
                ["updated"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Unexpected argument: " + list[i]);
                var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._values[list[i]] = hasValue ? list[++i] : null;
            }

            return options;
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;
    }
}
