using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// The engine server (engine/EngineServer.cs, Services/EngineServerPool.cs): every served command must give exactly what
// its own engine process gives (stdout, stderr, exit code, the saved profile), after game data changes the server must
// not answer from old lists, and no server may outlive its use. Real engine of TestEngine.Configuration.
// JOYPRO_SERVER_TEST_PROFILE=<profile> runs the equivalence sequence on a copy of that profile as well.
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class EngineServerTests
{
    private static string? EnginePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    [Theory]
    [InlineData("--status|false|relations|show|Pitch", true)]
    [InlineData("--profile|x.pr0file|--status|false|--history-note|Adding relation|relations|add|Gear", true)]
    [InlineData("--status|false|--text|binds|list", true)]
    [InlineData("--status|false|dcs|export|--path|x", true)]
    [InlineData("--status|false|dcs|refresh|id", false)]
    [InlineData("--status|false|warthunder|export|--path|x", false)]
    [InlineData("--status|false|relations|edit", false)]
    [InlineData("--status|false|relations|plan|x", false)]
    [InlineData("--editor|notepad|relations|show|x", false)]
    [InlineData("--status|false|--benchmark|false|version", false)]
    [InlineData("--status|false|arrange|prefs", true)]
    [InlineData("--status|false|arrange|ask|trim on the hat|--llm|off", true)]
    [InlineData("--status|false|arrange|ask|trim on the hat|--llm|auto", false)]
    [InlineData("--status|false|arrange|apply|--proposal|x", false)]
    [InlineData("--profile|x.pr0file|--status|false|profile|auto-added", true)]
    [InlineData("--profile|x.pr0file|--status|false|setup|games|--games|DCS|--status-only", false)]
    public void ServedCommands_AreTheQuickInteractiveOnes(string commandLine, bool served)
    {
        Assert.Equal(served, EngineServerCommands.IsServed(commandLine.Split('|')));
    }

    [Fact]
    public void ProcessCommands_ThatCanChangeGameData_RestartTheServer()
    {
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "--benchmark", "false", "version"]));
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "llm", "status"]));
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "relations", "show", "x"]));
        Assert.False(EngineServerCommands.KeepsServer(["--status", "false", "dcs", "refresh", "id"]));
        Assert.False(EngineServerCommands.KeepsServer(["--status", "false", "warthunder", "refresh-db"]));
        Assert.False(EngineServerCommands.KeepsServer(["--status", "false", "db", "check-updates", "--accept-all"]));
        Assert.False(EngineServerCommands.KeepsServer(["--status", "false", "games", "install", "set", "DCS", "x"]));
        // Exports and imports change game files and the profile, not the lists the server keeps.
        Assert.True(EngineServerCommands.KeepsServer(["--profile", "p", "--status", "false", "dcs", "export", "--path", "x"]));
        Assert.True(EngineServerCommands.KeepsServer(["--profile", "p", "--status", "false", "warthunder", "import"]));
        // The Start page's background look at the games only reads: the server keeps running after it.
        Assert.True(EngineServerCommands.KeepsServer(["--profile", "p", "--status", "false", "setup", "games", "--games", "DCS", "--status-only"]));
        Assert.False(EngineServerCommands.KeepsServer(["--profile", "p", "--status", "false", "setup", "apply", "--plan", "x"]));
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "--text", "db", "planes", "--game", "DCS"]));
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "--text", "db", "games"]));
        Assert.False(EngineServerCommands.KeepsServer(["--status", "false", "db", "check-updates", "--accept-all"]));
    }

    [Fact]
    public async Task ServedCommands_GiveWhatTheirOwnProcessGives()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "server-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CompareAsync(engine, root, "fresh", null);
            string? real = Environment.GetEnvironmentVariable("JOYPRO_SERVER_TEST_PROFILE");
            if (!string.IsNullOrWhiteSpace(real) && File.Exists(real))
                await CompareAsync(engine, root, "real", real);
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    private static async Task CompareAsync(string engine, string root, string name, string? source)
    {
        string processSide = Path.Combine(root, name + "-process");
        string serverSide = Path.Combine(root, name + "-server");
        List<List<string>> sequence =
        [
            ["relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50"],
            ["relations", "show", "Pitch"],
            ["relations", "add", "Gear", "--button", "--node", "DCS:d68pnilunilcdnilvdnilvpnilvunil:F-16C_50"],
            ["relations", "list"],
            ["relations", "group", "add", "Gear", "Landing"],
            ["relations", "rename", "Gear", "Gear lever"],
            ["relations", "show", "Gear lever"],
            ["relations", "aircraft", "Pitch"],
            ["relations", "search", "pitch", "--axis", "--limit", "20", "--per-game", "4"],
            ["relations", "browse", "--game", "DCS", "--plane", "F-16C_50"],
            ["relations", "suggest", "Pitch"],
            ["binds", "set", "Gear lever", "--device", "vJoy Device {89D63500-B53F-11f1-8002-444553540000}", "--input", "JOY_BTN7"],
            ["binds", "list"],
            ["binds", "modifier", "list"],
            ["relations", "show", "does not exist"],
            ["relations", "item", "remove", "Pitch", "--node", "DCS:a2001cdnil:F-16C_50"],
            ["relations", "check-aircraft"],
            ["profile", "auto-added"],
            ["layout", "titles"],
            ["arrange", "prefs"],
            ["arrange", "ask", "trim on the hat, curves softer for helis", "--llm", "off", "--games", "DCS", "--text"],
            ["profile", "save"],
            ["relations", "list"]
        ];
        List<string> processOutputs = await RunSideAsync(engine, processSide, source, sequence, server: false);
        List<string> serverOutputs = await RunSideAsync(engine, serverSide, source, sequence, server: true);
        string? dump = Environment.GetEnvironmentVariable("JOYPRO_SERVER_TEST_DUMP");
        for (int i = 0; i < sequence.Count && !string.IsNullOrWhiteSpace(dump); i++)
        {
            if (processOutputs[i] == serverOutputs[i])
                continue;
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, name + "-" + i + "-process.txt"), processOutputs[i]);
            File.WriteAllText(Path.Combine(dump, name + "-" + i + "-server.txt"), serverOutputs[i]);
        }
        for (int i = 0; i < sequence.Count; i++)
            Assert.True(processOutputs[i] == serverOutputs[i], name + ": '" + string.Join(" ", sequence[i]) + "' differs:\nprocess: " + Cut(processOutputs[i]) + "\nserver:  " + Cut(serverOutputs[i]));
        byte[] processProfile = File.ReadAllBytes(Path.Combine(processSide, "p.pr0file"));
        byte[] serverProfile = File.ReadAllBytes(Path.Combine(serverSide, "p.pr0file"));
        Assert.True(processProfile.SequenceEqual(serverProfile), name + ": the saved profiles differ (" + processProfile.Length + " / " + serverProfile.Length + " bytes)");
    }

    private static string Cut(string text) => text.Length > 1500 ? text[..1500] + "..." : text;

    private static async Task<List<string>> RunSideAsync(string engine, string folder, string? source, List<List<string>> sequence, bool server)
    {
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "p.pr0file");
        if (source != null)
            File.Copy(source, profile, overwrite: true);
        string data = Path.Combine(folder, "data");
        Dictionary<string, string> environment = new() { ["JOYPRO_DATA_DIR"] = data, ["JOYPRO_DEVICE_KB"] = "off" };
        EngineCliClient client = new();
        List<string> outputs = [];
        foreach (List<string> command in sequence)
        {
            List<string> args = ["--profile", profile, "--status", "false", "--benchmark", "false", "--load-corrections", "false"];
            args.AddRange(command);
            EngineRunOptions options = new() { Environment = environment };
            EngineCommandResult result;
            if (server)
            {
                EngineCommandResult? served = await EngineServerPool.TryRunAsync(engine, args, "test", options, CancellationToken.None);
                Assert.NotNull(served);
                result = served!;
            }
            else
            {
                result = await client.RunProcessAsync(engine, args, options, CancellationToken.None);
            }
            outputs.Add("exit " + result.ExitCode + "\n" + Normalize(result.StandardOutput, folder) + "\n--stderr--\n" + Normalize(result.StandardError, folder));
        }
        return outputs;
    }

    // The side's folder and the time stamps of the answer (GeneratedUtc) are all that may differ.
    private static string Normalize(string text, string folder) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(NormalizePaths(text, folder), "\"(GeneratedUtc|CreatedUtc|RecordedUtc)\":\"[^\"]*\"", "\"$1\":\"<time>\""),
            "\"(\\w*Milliseconds)\":\\d+", "\"$1\":0");

    private static string NormalizePaths(string text, string folder) =>
        (text ?? string.Empty).Replace(folder, "<side>", StringComparison.OrdinalIgnoreCase).Replace(folder.Replace('\\', '/'), "<side>", StringComparison.OrdinalIgnoreCase).Replace("\r\n", "\n");

    // A server that ran an export and Lay out with AI answers a search, the catalog and the relation list exactly as a
    // fresh server does, saves nothing on a question, and a pause of a few seconds keeps its working set. (The gate's
    // session after the exports was 2-3x slower: after the DCS export every later command of that server saved the
    // profile, 0.4 s each and a profile version per click; and the server gave its memory back after 3 s quiet.)
    [Fact]
    public async Task AfterAnExport_TheServerAnswersAsAFreshOne_AndAShortPauseKeepsItsMemory()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "server-after-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            List<List<string>> setup =
            [
                ["relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50"],
                ["relations", "add", "Gear", "--button", "--node", "DCS:d68pnilunilcdnilvdnilvpnilvunil:F-16C_50"],
                ["binds", "set", "Gear", "--device", "vJoy Device {89D63500-B53F-11f1-8002-444553540000}", "--input", "JOY_BTN7"],
                ["dcs", "export", "--path", "<saved>", "--mode", "clean", "--keep-keyboard-defaults", "true", "--nuke-untouched-devices", "true", "--clean-all-aircraft", "false"],
                ["arrange", "ask", "trim on the hat, curves softer for helis", "--llm", "off", "--games", "DCS", "--text"]
            ];
            List<List<string>> probes =
            [
                ["relations", "search", "pitch", "--limit", "200", "--per-game", "12"],
                ["relations", "search", "Pitch (left stick up/down)", "--limit", "200", "--per-game", "12"],
                ["relations", "browse", "--game", "DCS", "--plane", "F-16C_50"],
                ["relations", "browse", "--game", "AceCombat8"],
                ["relations", "list", "--titles"],
                ["relations", "check-aircraft"],
                ["binds", "list"]
            ];
            // The used server: everything in one server.
            List<string> used = await RunAfterExportAsync(engine, Path.Combine(root, "used"), setup, probes, setupInServer: true);
            int server = ServerProcesses(engine).FirstOrDefault();
            Assert.True(server != 0, "no engine server runs");
            await Task.Delay(4500);
            using (Process process = Process.GetProcessById(server))
            {
                process.Refresh();
                Assert.True(process.WorkingSet64 > 40L * 1024 * 1024,
                    "the server gave its working set back after a short pause (" + (process.WorkingSet64 >> 20) + " MB): every next call pages it in again");
            }
            EngineServerPool.StopAll();
            // A fresh server for the questions; the same changes made in processes of their own before.
            List<string> fresh = await RunAfterExportAsync(engine, Path.Combine(root, "fresh"), setup, probes, setupInServer: false);
            for (int i = 0; i < probes.Count; i++)
                Assert.True(fresh[i] == used[i], "'" + string.Join(" ", probes[i]) + "' after an export differs from a fresh server:\nfresh: " + Cut(fresh[i]) + "\nused:  " + Cut(used[i]));
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    private static async Task<List<string>> RunAfterExportAsync(string engine, string folder, List<List<string>> setup, List<List<string>> probes, bool setupInServer)
    {
        string saved = Path.Combine(folder, "Saved Games DCS");
        Directory.CreateDirectory(Path.Combine(saved, "Config", "Input"));
        string profile = Path.Combine(folder, "p.pr0file");
        Dictionary<string, string> environment = new()
        {
            ["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data"), ["JOYPRO_DEVICE_KB"] = "off", ["JOYPRO_TEST_ASSUME_INSTALLED"] = "1"
        };
        EngineRunOptions options = new() { Environment = environment };
        EngineCliClient client = new();
        async Task<EngineCommandResult> Run(List<string> command, bool inServer)
        {
            List<string> args = ["--profile", profile, "--status", "false", "--benchmark", "false", "--load-corrections", "false"];
            args.AddRange(command.Select(a => a == "<saved>" ? saved : a));
            if (!inServer)
                return await client.RunProcessAsync(engine, args, options, CancellationToken.None);
            EngineCommandResult? served = await EngineServerPool.TryRunAsync(engine, args, "test", options, CancellationToken.None);
            Assert.NotNull(served);
            return served!;
        }
        foreach (List<string> command in setup)
        {
            EngineCommandResult result = await Run(command, setupInServer);
            Assert.True(result.ExitCode == 0, string.Join(" ", command) + ": " + result.StandardError);
        }
        List<string> outputs = [];
        DateTime savedAt = File.GetLastWriteTimeUtc(profile);
        foreach (List<string> command in probes)
        {
            EngineCommandResult result = await Run(command, inServer: true);
            outputs.Add("exit " + result.ExitCode + "\n" + Normalize(result.StandardOutput, folder));
        }
        // Questions never save the profile (after a served DCS export, every later command of that server saved it).
        Assert.True(File.GetLastWriteTimeUtc(profile) == savedAt, "a read-only command saved the profile (" + (setupInServer ? "used" : "fresh") + " server)");
        return outputs;
    }

    [Fact]
    public async Task ChangedGameData_StartsANewServer_AndTheAnswerUsesTheNewFiles()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "server-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(root, "data");
            Dictionary<string, string> environment = new() { ["JOYPRO_DATA_DIR"] = data };
            EngineRunOptions options = new() { Environment = environment };
            List<string> args = ["--status", "false", "relations", "functions"];
            EngineCommandResult? first = await EngineServerPool.TryRunAsync(engine, args, "test", options, CancellationToken.None);
            Assert.NotNull(first);
            Assert.Equal(0, first!.ExitCode);
            int starts = EngineServerPool.Starts;
            // Same data: the same server answers.
            EngineCommandResult? again = await EngineServerPool.TryRunAsync(engine, args, "test", options, CancellationToken.None);
            Assert.Equal(0, again!.ExitCode);
            Assert.Equal(starts, EngineServerPool.Starts);
            // A game list in the data folder changes (as a refresh does): the kept lists are old, a new server answers.
            string db = Path.Combine(data, "GameData", "DB");
            string? list = Directory.Exists(db) ? Directory.EnumerateFiles(db, "*", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (list == null)
            {
                Directory.CreateDirectory(Path.Combine(db, "DCS"));
                list = Path.Combine(db, "DCS", "server-test.txt");
                File.WriteAllText(list, "new");
            }
            File.SetLastWriteTimeUtc(list, File.GetLastWriteTimeUtc(list).AddSeconds(5));
            EngineCommandResult? after = await EngineServerPool.TryRunAsync(engine, args, "test", options, CancellationToken.None);
            Assert.True(after!.ExitCode == 0, after.StandardError);
            Assert.Equal(starts + 1, EngineServerPool.Starts);
            string? dump = Environment.GetEnvironmentVariable("JOYPRO_SERVER_TEST_DUMP");
            if (!string.IsNullOrWhiteSpace(dump) && first.StandardOutput != after.StandardOutput)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, "stale-first.txt"), first.StandardOutput);
                File.WriteAllText(Path.Combine(dump, "stale-after.txt"), after.StandardOutput);
            }
            Assert.Equal(Normalize(first.StandardOutput, root), Normalize(after.StandardOutput, root));
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Servers_EndWithTheirUse_NoEngineLeftBehind()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "server-end-" + Guid.NewGuid().ToString("N"));
        try
        {
            EngineRunOptions options = new() { Environment = new Dictionary<string, string> { ["JOYPRO_DATA_DIR"] = Path.Combine(root, "data") } };
            EngineCommandResult? result = await EngineServerPool.TryRunAsync(engine, ["--status", "false", "relations", "functions"], "test", options, CancellationToken.None);
            Assert.Equal(0, result!.ExitCode);
            Assert.Contains(ServerProcesses(engine), p => true);
            EngineServerPool.Recycle("test");
            Stopwatch watch = Stopwatch.StartNew();
            while (ServerProcesses(engine).Count > 0 && watch.ElapsedMilliseconds < 10000)
                await Task.Delay(100);
            Assert.Empty(ServerProcesses(engine));
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    // Engine servers this test process started that still run.
    private static List<int> ServerProcesses(string engine)
    {
        List<int> started;
        lock (EngineServerProcess.StartedProcessIds)
            started = EngineServerProcess.StartedProcessIds.ToList();
        List<int> running = [];
        foreach (int id in started)
        {
            try
            {
                using Process p = Process.GetProcessById(id);
                if (!p.HasExited && string.Equals(p.MainModule?.FileName, engine, StringComparison.OrdinalIgnoreCase))
                    running.Add(id);
            }
            catch (Exception)
            {
                // Gone.
            }
        }
        return running;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
