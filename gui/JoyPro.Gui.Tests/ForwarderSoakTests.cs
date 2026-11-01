using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JoyPro.Engine.VJoy;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The forwarder's cost while it runs permanently (docs/virtual-joystick.md "Performance"; the user's rule: everything
// running permanently must be unnoticeable and never affect the game). The real JoyPro.VJoy.exe runs with the dry sink
// and synthetic devices (--fake-sources): "idle" (nothing changes, devices that signal changes: the loop rests),
// "stream" (4 devices change every pass, 50 virtual functions of every type, chained and with modes) and "noisy" (what
// real sticks do while nobody touches them, seen in the vJoy live test 2026-11-01: one device signals a change 1000
// times a second with one count of noise on a mapped axis - the loop must stay at one pass and one wake-up per period,
// not one per report). From its own stats
// lines: CPU in % of one core, private bytes and handles (no growth = no leak), the loop thread's allocations (none
// per pass), the pass rate and the process priority (below normal).
// Quick run (always): 30 s per phase (12 s warm-up: the JIT tiers up). Long run: JOYPRO_FORWARDER_SOAK=1 (5 minutes per phase, or
// JOYPRO_FORWARDER_SOAK_SECONDS). The numbers go to the test output and %TEMP%\JoyPro2.Gui.Tests\forwarder-soak.txt.
public sealed class ForwarderSoakTests(ITestOutputHelper output)
{
    // Budgets (CPU of one core). Measured on the development PC: see docs/virtual-joystick.md "Performance".
    private const double IdleCpuBudgetPercent = 0.5;
    private const double StreamCpuBudgetPercent = 1.5;
    private const long MemoryGrowthBudgetKb = 1024;
    private const int HandleGrowthBudget = 5;

    // JOYPRO_FORWARDER_EXE (e.g. the release build), else the newest build of gui/JoyPro.VJoy.
    private static string? ForwarderExe()
    {
        string? configured = Environment.GetEnvironmentVariable("JOYPRO_FORWARDER_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder != null && !Directory.Exists(Path.Combine(folder.FullName, "gui", "JoyPro.VJoy")))
            folder = folder.Parent;
        if (folder == null)
            return null;
        return new[] { "Release", "Debug" }
            .Select(c => Path.Combine(folder.FullName, "gui", "JoyPro.VJoy", "bin", c, "net9.0-windows", "JoyPro.VJoy.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // 4 synthetic sources, a slot per axis/button/hat kind, 50 functions of every type plus a chained pair in two modes.
    private static string Config(string folder)
    {
        List<VirtualFunction> functions = VfBenchmark.Mix(50, 4);
        VirtualFunction bands = functions.First(f => f.Type == VirtualFunctionCatalog.Bands);
        VirtualFunction chained = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Toggle, "Chained toggle");
        chained.Id = "c1";
        chained.Modes = ["Combat"];
        chained.InputOf("in")!.Device = VfChain.Device(bands.Id);
        chained.InputOf("in")!.Input = "BAND2";
        chained.OutputOf("out")!.Target = "JOY_BTN128";
        chained.OutputOf("out")!.VJoyDevice = 1;
        VfResolver.Resolve(chained, ["BENCH0", "BENCH1", "BENCH2", "BENCH3"], [1]);
        functions.Add(chained);
        VirtualFunction cycle = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.ModeSwitch, "Mode cycle");
        cycle.Id = "c2";
        cycle.Params["action"] = VirtualFunctionCatalog.ModeActionCycle;
        cycle.SwitchModes = ["Default", "Combat"];
        cycle.InputOf("in")!.Device = "BENCH0";
        cycle.InputOf("in")!.Input = "JOY_BTN3";
        VfResolver.Resolve(cycle, ["BENCH0", "BENCH1", "BENCH2", "BENCH3"], [1]);
        functions.Add(cycle);
        var config = new
        {
            version = 1,
            vjoyDevices = new[] { 1 },
            sources = Enumerable.Range(0, 4).Select(i => new { device = "BENCH" + i, name = "BENCH" + i, instanceGuid = "00000000-0000-0000-0000-00000000000" + (i + 1) }).ToArray(),
            slots = new object[]
            {
                new { source = 0, kind = "axis", index = 0, device = 1, target = 0 },
                new { source = 1, kind = "axis", index = 1, device = 1, target = 1 },
                new { source = 2, kind = "button", index = 0, device = 1, target = 0 },
                new { source = 3, kind = "hat", index = 0, device = 1, target = 0 }
            },
            virtualGames = new[] { "DCS" },
            xboxGame = "",
            xbox = Array.Empty<object>(),
            functions,
            modes = new[] { new VfModeSet { Group = "", Modes = [new VfMode { Name = "Default" }, new VfMode { Name = "Combat", Parent = "Default" }], Start = "Default" } }
        };
        string path = Path.Combine(folder, "forwarder-soak.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    private sealed record Stats(double AtMs, long CpuMs, long PrivateKb, long Handles, long LoopAlloc, double Hz, bool Idle, long Passes, long Wakes, long Writes);

    private static Stats? Parse(string line, double atMs)
    {
        if (!line.StartsWith("stats ", StringComparison.Ordinal))
            return null;
        Dictionary<string, string> v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        long L(string key) => v.TryGetValue(key, out string? s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : -1;
        // cycles-ms: the CPU from the process's cycle counter (exact); cpu-ms is sampled at the clock tick and charges a
        // loop that runs for microseconds at random (0.000 % or 0.5 % over 20 s).
        long cpu = L("cycles-ms") >= 0 ? L("cycles-ms") : L("cpu-ms");
        return new Stats(atMs, cpu, L("private-kb"), L("handles"), L("loop-alloc"), L("hz"), v.GetValueOrDefault("idle") == "1", L("passes"), L("wakes"), L("writes"));
    }

    private (List<Stats> Stats, string Ready, string Priority) Run(string exe, string config, string fake, int seconds)
    {
        ProcessStartInfo info = new(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "run", "--config", config, "--sink", "dry", "--rate", "144", "--fake-sources", fake, "--stats-seconds", "1", "--duration", seconds.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_VJOY_DRY"] = "1";
        using Process process = Process.Start(info)!;
        Stopwatch clock = Stopwatch.StartNew();
        List<Stats> stats = [];
        string ready = string.Empty;
        string priority = string.Empty;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            if (line.StartsWith("ready ", StringComparison.Ordinal))
            {
                ready = line;
                try
                {
                    priority = process.PriorityClass.ToString();
                }
                catch (InvalidOperationException)
                {
                }
            }
            Stats? s = Parse(line, clock.Elapsed.TotalMilliseconds);
            if (s != null)
                stats.Add(s);
        }
        process.WaitForExit(10_000);
        return (stats, ready, priority);
    }

    [Fact]
    public void TheForwarderStaysUnnoticeable()
    {
        string? exe = ForwarderExe();
        Assert.True(exe != null, "JoyPro.VJoy.exe is not built (dotnet build gui/JoyPro.VJoy).");
        bool longRun = Environment.GetEnvironmentVariable("JOYPRO_FORWARDER_SOAK") == "1";
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("JOYPRO_FORWARDER_SOAK_SECONDS"), out int configured) && configured >= 10 ? configured : longRun ? 300 : 30;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string config = Config(folder);
        StringBuilder report = new();
        report.AppendLine("JoyPro.VJoy soak, " + seconds + " s per phase, 144 Hz, 4 synthetic devices (noisy: one at 1000 Hz), 52 virtual functions, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", " + exe);
        foreach ((string fake, double budget) in new[] { ("idle", IdleCpuBudgetPercent), ("stream", StreamCpuBudgetPercent), ("noisy", StreamCpuBudgetPercent) })
        {
            (List<Stats> stats, string ready, string priority) = Run(exe!, config, fake, seconds);
            Assert.Contains("priority=below-normal", ready);
            Assert.Contains("functions=52", ready);
            Assert.Equal("BelowNormal", priority);
            // After a warm-up of 5 s (JIT, first passes).
            List<Stats> steady = stats.Where(s => s.AtMs >= (seconds >= 60 ? 30000 : 12000)).ToList();
            Assert.True(steady.Count >= 3, fake + ": too few stats lines: " + stats.Count);
            Stats first = steady[0];
            Stats last = steady[^1];
            double cpu = (last.CpuMs - first.CpuMs) * 100.0 / Math.Max(1, last.AtMs - first.AtMs);
            long memory = last.PrivateKb - first.PrivateKb;
            long handles = last.Handles - first.Handles;
            long alloc = last.LoopAlloc - first.LoopAlloc;
            double hz = steady.Average(s => s.Hz);
            double seconds2 = Math.Max(0.001, (last.AtMs - first.AtMs) / 1000.0);
            double wakesPerSecond = (last.Wakes - first.Wakes) / seconds2;
            double writesPerSecond = (last.Writes - first.Writes) / seconds2;
            string line = fake + ": CPU " + cpu.ToString("0.000", CultureInfo.InvariantCulture) + " % of one core (budget " + budget.ToString("0.0", CultureInfo.InvariantCulture) + " %), " +
                          hz.ToString("0", CultureInfo.InvariantCulture) + " passes/s, private " + last.PrivateKb + " kB (" + (memory >= 0 ? "+" : "") + memory + " kB), handles " + last.Handles +
                          " (" + (handles >= 0 ? "+" : "") + handles + "), loop allocations " + alloc + " bytes after warm-up, idle=" + last.Idle +
                          ", wake-ups/s " + wakesPerSecond.ToString("0", CultureInfo.InvariantCulture) + ", vJoy writes/s " + writesPerSecond.ToString("0", CultureInfo.InvariantCulture);
            output.WriteLine(line);
            report.AppendLine(line);
            Assert.True(cpu <= budget, line);
            Assert.True(memory <= MemoryGrowthBudgetKb, "Memory grows: " + line);
            Assert.True(handles <= HandleGrowthBudget, "Handles grow: " + line);
            Assert.Equal(0, alloc);
            if (fake == "idle")
            {
                Assert.True(last.Idle, "The loop must rest when nothing changes: " + line);
                Assert.True(hz <= 15, "Idle passes/s: " + line);
            }
            else
                Assert.True(hz >= 140, "The rate must hold while devices change: " + line);
            // At most one pass per period (144 Hz) however often a device reports, one wake-up per pass.
            Assert.True(hz <= 150, "More passes than the rate: " + line);
            if (fake == "noisy")
            {
                Assert.True(wakesPerSecond <= hz * 1.2 + 5, "A wake-up per report instead of per period: " + line);
                Assert.True(writesPerSecond <= hz + 1, "More vJoy writes than passes: " + line);
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "forwarder-soak.txt"), report.ToString());
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }
}
