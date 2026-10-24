using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// Many actions at once in the relation editor (a user added pitch of many games and aircraft: JoyPro hung, and every
// change afterwards took long):
// - the editor's own work for 500 actions (the engine faked, answering at once): opening, adding 500 ticked rows in
//   one engine call, the list changed once per load instead of once per action;
// - single Add clicks in a row go to the engine together instead of one engine call and reload per click;
// - the real engine: 500 actions added in one call, the suggestions of such a relation, and a save afterwards.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class RelationEditorPerfTests
{
    private readonly ITestOutputHelper output;

    public RelationEditorPerfTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task FiveHundredActions_OpenAndAddInOneGo_TheEditorStaysFast()
    {
        BulkFakeEngine fake = new(initialNodes: 40, searchRows: 500);
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-perf.pr0file");
        int listChanges = 0;
        vm.RelationNodes.CollectionChanged += (_, _) => listChanges++;

        Stopwatch watch = Stopwatch.StartNew();
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Pitch");
        await IdleAsync(vm, () => vm.RelationNodes.Count == 40);
        long open = watch.ElapsedMilliseconds;

        vm.ShowBuilderFindCommand.Execute(null);
        vm.BuilderSearchText = "pitch";
        await IdleAsync(vm, () => vm.BuilderSearchResults.Count == 500);
        foreach (BuilderSearchResultItem row in vm.BuilderSearchResults.Where(r => r.CanCheck))
            row.IsChecked = true;
        Assert.Equal(500, vm.BuilderCheckedCount);
        listChanges = 0;
        fake.Calls.Clear();
        watch.Restart();
        await vm.AddCheckedBuilderResultsAsync();
        await IdleAsync(vm, () => vm.RelationNodes.Count == 540);
        long add = watch.ElapsedMilliseconds;
        output.WriteLine("open 40 actions: " + open + " ms; add 500 ticked rows: " + add + " ms; list changes " + listChanges);

        // One engine call adds all of them.
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.Equal(500, fake.Calls.Single(c => AxisTuningTests.Seq(c, "relations", "suggest", "apply")).Count(a => a == "--add"));
        // The list of actions changes once per load, not once per action.
        Assert.True(listChanges <= 3, "the action list changed " + listChanges + " times for one add");
        Assert.True(vm.BuilderSearchResults.All(r => r.IsInOpenRelation), "rows of the add are not marked as in the relation");
        Assert.True(open < 5000, "opening a relation took " + open + " ms");
        Assert.True(add < 8000, "adding 500 actions took " + add + " ms in the editor");
        await RelationBuilderTests.SettleAllAsync();
    }

    [Fact]
    public async Task SingleAddClicksInARow_GoToTheEngineTogether()
    {
        BulkFakeEngine fake = new(initialNodes: 3, searchRows: 0, suggestions: 8) { ApplyGate = new TaskCompletionSource() };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-perf-clicks.pr0file");
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Pitch");
        await IdleAsync(vm, () => vm.BuilderSuggestions.Count == 8);
        fake.Calls.Clear();

        List<BuilderSuggestionItem> rows = [.. vm.BuilderSuggestions];
        // The first click starts the engine (held at the gate); the next ones are clicked meanwhile.
        List<Task> clicks = [];
        foreach (BuilderSuggestionItem row in rows)
            clicks.Add(vm.AddBuilderSuggestionCommand.ExecuteAsync(row));
        Assert.Empty(vm.BuilderSuggestions);
        fake.ApplyGate.SetResult();
        await Task.WhenAll(clicks);
        await IdleAsync(vm, () => vm.RelationNodes.Count == 11);

        List<IReadOnlyList<string>> applies = fake.Calls.Where(c => AxisTuningTests.Seq(c, "relations", "suggest", "apply")).ToList();
        output.WriteLine("8 clicks: " + applies.Count + " apply calls, " + fake.Calls.Count(c => AxisTuningTests.Seq(c, "relations", "show")) + " reloads");
        Assert.Equal(2, applies.Count);
        Assert.Equal(8, applies.Sum(a => a.Count(x => x == "--add")));
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "show"));
        Assert.Equal(11, vm.RelationNodes.Count);
        Assert.Contains("8 changes from the suggestions applied.", vm.StatusText);
        await RelationBuilderTests.SettleAllAsync();
    }

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    // The real engine: a relation gets 500 actions of every game in one call; its suggestions and a later save stay fast.
    [Fact]
    public async Task RealEngine_FiveHundredActionsInOneCall_SuggestAndSaveStayFast()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "relperf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DataDirEngineClient client = new(Path.Combine(folder, "data"));
            string profile = Path.Combine(folder, "perf.pr0file");
            // 500 axis actions of the lists this engine has (every game and aircraft).
            List<string> specs = [];
            foreach (string query in new[] { "pitch", "roll", "axis", "throttle", "yaw", "zoom", "brake", "trim", "elevator", "rudder", "collective", "slew" })
            {
                EngineCommandResult search = await client.RunAsync(engine, ["--profile", profile, "relations", "search", query, "--axis", "--limit", "400"]);
                Assert.True(search.ExitCode == 0, search.StandardError);
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(search.StandardOutput));
                foreach (System.Text.Json.JsonElement hit in doc.RootElement.GetProperty("Results").EnumerateArray())
                {
                    string game = hit.GetProperty("Game").GetString() ?? string.Empty;
                    string id = hit.GetProperty("Id").GetString() ?? string.Empty;
                    List<string> planes = hit.GetProperty("Planes").EnumerateArray().Select(p => p.GetString() ?? string.Empty).Where(p => p.Length > 0).ToList();
                    string spec = game + ":" + id + ":" + string.Join("|", planes.Count > 0 ? planes : [game]);
                    if (!specs.Contains(spec))
                        specs.Add(spec);
                }
                if (specs.Count >= 500)
                    break;
            }
            specs = specs.Take(500).ToList();
            Assert.True(specs.Count >= 100, "the engine's lists gave only " + specs.Count + " axis actions");

            Assert.Equal(0, (await client.RunAsync(engine, ["--profile", profile, "relations", "add", "Big", "--axis", "--node", specs[0]])).ExitCode);
            List<string> apply = ["--profile", profile, "relations", "suggest", "apply", "Big"];
            foreach (string spec in specs.Skip(1))
                apply.AddRange(["--add", spec]);
            // Usually longer than a Windows command line holds (32767 characters; DCS rows carry their aircraft): the
            // client passes it in an argument file (EngineCliClient.ArgsFileThreshold).
            int commandLine = EngineCliClient.CommandLineLength(apply);
            Stopwatch watch = Stopwatch.StartNew();
            EngineCommandResult added = await client.RunAsync(engine, apply);
            long addMs = watch.ElapsedMilliseconds;
            Assert.True(added.ExitCode == 0, added.StandardError);

            watch.Restart();
            EngineCommandResult suggest = await client.RunAsync(engine, ["--profile", profile, "relations", "suggest", "Big", "--min", "low"]);
            long suggestMs = watch.ElapsedMilliseconds;
            Assert.True(suggest.ExitCode == 0, suggest.StandardError);
            using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(suggest.StandardOutput)))
                Assert.Equal("Big", doc.RootElement.GetProperty("Relation").GetString());

            // A small change afterwards: the save of the profile with the big relation.
            watch.Restart();
            EngineCommandResult rename = await client.RunAsync(engine, ["--profile", profile, "relations", "rename", "Big", "Big axes"]);
            long saveMs = watch.ElapsedMilliseconds;
            Assert.True(rename.ExitCode == 0, rename.StandardError);
            EngineCommandResult show = await client.RunAsync(engine, ["--profile", profile, "--text", "relations", "show", "Big axes"]);
            int nodes = show.StandardOutput.Split('\n').Count(l => l.TrimStart().StartsWith('['));
            output.WriteLine(specs.Count + " actions (" + nodes + " nodes, command line " + commandLine + " chars): add " + addMs + " ms, suggest " + suggestMs + " ms, save " + saveMs + " ms");
            Assert.True(nodes >= specs.Count * 0.9, "only " + nodes + " of " + specs.Count + " actions arrived");
            Assert.True(addMs < 20000, "adding " + specs.Count + " actions took " + addMs + " ms");
            Assert.True(suggestMs < 20000, "the suggestions of the big relation took " + suggestMs + " ms");
            Assert.True(saveMs < 10000, "a save after the big add took " + saveMs + " ms");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task IdleAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        await Task.Delay(50);
        int idle = 0;
        for (int i = 0; i < 2000 && (idle < 5 || !condition()); i++)
        {
            idle = vm.IsBusy || vm.IsBuilderSuggestLoading || vm.IsBuilderSearching ? 0 : idle + 1;
            await Task.Delay(10);
        }
        Assert.True(condition(), "the editor did not get there");
    }

    // An axis relation "Pitch" with DCS actions of many aircraft; the search finds searchRows more, the suggestions
    // list `suggestions` rows. Answers at once (apply waits for ApplyGate when it is set).
    private sealed class BulkFakeEngine : IEngineCliClient
    {
        private static readonly string[] Planes = Enumerable.Range(1, 30).Select(i => "Plane" + i.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        private readonly List<(string Game, string Id, string Planes)> nodes = [];
        private readonly int searchRows;
        private readonly int suggestions;

        public BulkFakeEngine(int initialNodes, int searchRows, int suggestions = 0)
        {
            for (int i = 0; i < initialNodes; i++)
                nodes.Add(("DCS", "a" + i.ToString(CultureInfo.InvariantCulture), string.Join(",", Planes.Take(5 + i % 20))));
            this.searchRows = searchRows;
            this.suggestions = suggestions;
        }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public TaskCompletionSource? ApplyGate { get; set; }

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            if (ApplyGate != null && AxisTuningTests.Seq(arguments, "relations", "suggest", "apply"))
                await ApplyGate.Task;
            return new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = Output(arguments), StandardError = string.Empty, Arguments = arguments.ToArray() };
        }

        private static string Spec(string game, string id, string planes) => game + ":" + id + ":" + planes.Replace(',', '|');

        private string Output(IReadOnlyList<string> args)
        {
            if (AxisTuningTests.Seq(args, "relations", "list"))
                return "{\"Relations\":[{\"Name\":\"Pitch\",\"IsAxis\":true,\"Groups\":[],\"Nodes\":[" +
                       string.Join(",", nodes.Select(n => "{\"Id\":\"" + n.Id + "\",\"Game\":\"" + n.Game + "\",\"Aircraft\":{" + string.Join(",", n.Planes.Split(',').Select(p => "\"" + p + "\":true")) + "}}")) + "],\"Bind\":null}]}";
            if (AxisTuningTests.Seq(args, "relations", "show"))
                return "Relation: Pitch" + Environment.NewLine + "Nodes:" + Environment.NewLine +
                       string.Join(Environment.NewLine, nodes.Select((n, i) => "  [" + (i + 1) + "] game=" + n.Game + ", id=" + n.Id + ", aircraft=" + n.Planes));
            if (AxisTuningTests.Seq(args, "relations", "suggest", "apply"))
            {
                for (int i = 0; i + 1 < args.Count; i++)
                {
                    if (args[i] != "--add")
                        continue;
                    string[] parts = args[i + 1].Split(':');
                    nodes.Add((parts[0], parts[1], parts[2].Replace('|', ',')));
                }
                return "Relation: Pitch" + Environment.NewLine + "Added: 1";
            }
            if (AxisTuningTests.Seq(args, "relations", "suggest"))
            {
                HashSet<string> have = nodes.Select(n => n.Game + ":" + n.Id).ToHashSet();
                IEnumerable<string> rows = Enumerable.Range(0, suggestions).Where(i => !have.Contains("IL2Game:s" + i)).Select(i =>
                    "{\"Game\":\"IL2Game\",\"Id\":\"s" + i + "\",\"Title\":\"Pitch " + i + "\",\"IsAxis\":true,\"Planes\":[\"IL2Game\"],\"Confidence\":\"high\",\"Score\":90,\"Source\":\"catalog\"," +
                    "\"Why\":\"Same function\",\"Reasons\":[],\"InOtherRelations\":[],\"Missing\":\"\",\"NewPlanes\":[]}");
                return "{\"Relation\":\"Pitch\",\"Slot\":\"pitch\",\"SlotName\":\"Pitch\",\"FunctionState\":\"named\",\"FunctionLabel\":\"Pitch\",\"IsAxis\":true,\"Functions\":[],\"Suggestions\":[" +
                       string.Join(",", rows) + "],\"Misfits\":[],\"Dismissed\":[],\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";
            }
            if (AxisTuningTests.Seq(args, "relations", "aircraft"))
            {
                StringBuilder sb = new("{\"Nodes\":[");
                sb.Append(string.Join(",", nodes.Where(n => n.Game == "DCS").Select(n => "{\"Game\":\"DCS\",\"Id\":\"" + n.Id + "\",\"Title\":\"Pitch\",\"Differs\":0,\"Excluded\":0,\"Aircraft\":[" +
                    string.Join(",", Planes.Select(p => "{\"Plane\":\"" + p + "\",\"Title\":\"Pitch\",\"Category\":\"Flight\",\"Match\":\"same\",\"Note\":\"\",\"State\":\"" + (n.Planes.Split(',').Contains(p) ? "on" : "off") + "\"}")) + "]}")));
                return sb.Append("]}").ToString();
            }
            if (AxisTuningTests.Seq(args, "relations", "search"))
            {
                IEnumerable<string> hits = Enumerable.Range(0, searchRows).Select(i =>
                    "{\"Game\":\"DCS\",\"Id\":\"b" + i + "\",\"Title\":\"Pitch axis " + i + "\",\"Category\":\"Flight\",\"IsAxis\":true,\"Planes\":[\"" + Planes[i % Planes.Length] + "\"],\"Score\":90," +
                    "\"Why\":\"\",\"InRelations\":[],\"Match\":\"words\",\"Function\":\"\"}");
                return "{\"Query\":\"pitch\",\"DidYouMean\":\"\",\"Functions\":[],\"FunctionGroups\":[],\"Results\":[" + string.Join(",", hits) + "],\"Total\":" + searchRows + "}";
            }
            if (AxisTuningTests.Seq(args, "binds", "list"))
                return "{\"Binds\":[]}";
            return "{}";
        }
    }
}
