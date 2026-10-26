using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tester reports on 2.4.2 (imacken, real engine, his profile's situation rebuilt with engine commands):
//   - "Move to another button": the AH-64D's "Master Warning Button" from "Button CP2 B1" to VPC Panel #2 BTN 3 said
//     "No relation uses that button yet: it gets its own relation" although "Button CP2 B3" is on BTN 3. B3 holds the
//     AH-64D's "Master Caution Button", and in a relation an aircraft has one action, so the engine skipped B3 and the
//     panel claimed no relation used the button. Now the panel names B3 and its action and offers swap / replace / keep
//     both. Pressing the new button also made the editor jump to B3 (follow input) while the panel named B1's action.
//   - The search found 21 relations for "Master" but showed only their names: now the matching actions show under the
//     card (the word marked, game and aircraft), and a click opens the action.
// Runs when the engine is built (engine\bin\x64\<configuration>\engine.exe); never shows a window.
[Collection("ProcessEnvironment")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MoveTakenButtonAndSearchMatchTests
{
    // His device as the profile stores it (DCS's spelling: "11f0" in lower case) and as another program spells it.
    private const string Panel = "VPC Panel #2 {03725130-D2C1-11f0-8003-444553540000}";
    private const string PanelOtherCase = "VPC Panel #2 {03725130-d2c1-11F0-8003-444553540000}";
    private const string ApacheWarning = "d3019pnilu3019cd11vd1vpnilvu0";
    private const string ApacheCaution = "d3018pnilu3018cd11vd1vpnilvu0";
    private const string Apaches = "AH-64D_BLK_II_CPG|AH-64D_BLK_II_PLT";
    private const string ViperCaution = "d3001pnilu3001cd12vd1vpnilvu0";
    private const string TigerStart = "d3001pnilu3001cd6vd1vpnilvu0";

    // His B1 / B3: Master Warning (AH-64D) and Master Caution (F-16C) on BTN 1, Master Caution (AH-64D) and an F-5E action on BTN 3.
    private static void BuildB1B3(string engine, TestFolder folder, string profile)
    {
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B1", "--button", "--node", "DCS:" + ApacheWarning + ":" + Apaches,
            "--node", "DCS:" + ViperCaution + ":F-16C_50", "--device", Panel, "--input", "JOY_BTN1");
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B3", "--button", "--node", "DCS:" + ApacheCaution + ":" + Apaches,
            "--node", "DCS:" + TigerStart + ":F-5E-3", "--device", Panel, "--input", "JOY_BTN3");
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B2", "--button", "--node", "MSFS2024:KEY_MASTER_WARNING_TOGGLE",
            "--device", Panel, "--input", "JOY_BTN2");
    }

    // The engine alone: a taken button is never "no relation uses it" (fails on 2.4.2: no Taken, Created=true).
    [Fact]
    public void Engine_TakenButton_NamesTheRelationAndAction_AndSwapReplaceKeepBothWork()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        foreach (string device in new[] { Panel, PanelOtherCase })
        {
            using TestFolder folder = new();
            string profile = folder.Profile();
            BuildB1B3(engine, folder, profile);
            string dry = Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + ApacheWarning + ":" + Apaches,
                "--device", device, "--input", "JOY_BTN3", "--dry-run");
            Assert.Contains("\"Taken\":true", dry);
            Assert.Contains("\"TakenRelation\":\"Button CP2 B3\"", dry);
            Assert.Contains("\"Created\":false", dry);
            // Without a choice nothing changes.
            Assert.NotEqual(0, EngineExit(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node",
                "DCS:" + ApacheWarning + ":" + Apaches, "--device", device, "--input", "JOY_BTN3"));
            // An aircraft without an action on BTN 3 joins B3 as before (F-16C).
            string viper = Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + ViperCaution + ":F-16C_50",
                "--device", device, "--input", "JOY_BTN3", "--dry-run");
            Assert.Contains("\"Joined\":true", viper);
            Assert.Contains("\"Taken\":false", viper);
        }

        // Swap: the two AH-64D actions trade buttons; the other aircraft stay.
        using (TestFolder folder = new())
        {
            string profile = folder.Profile();
            BuildB1B3(engine, folder, profile);
            Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + ApacheWarning + ":" + Apaches,
                "--device", PanelOtherCase, "--input", "JOY_BTN3", "--if-taken", "swap");
            string b1 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B1", "--text");
            string b3 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B3", "--text");
            Assert.Contains(ApacheCaution + ", aircraft=AH-64D_BLK_II_CPG, AH-64D_BLK_II_PLT", b1);
            Assert.DoesNotContain(ApacheWarning, b1);
            Assert.Contains(ViperCaution, b1);
            Assert.Contains(ApacheWarning + ", aircraft=AH-64D_BLK_II_CPG, AH-64D_BLK_II_PLT", b3);
            Assert.DoesNotContain(ApacheCaution, b3);
            Assert.Contains(TigerStart, b3);
            Assert.Contains("joystick=" + Panel + ",", b3);

            // (c) The next move after the first one sees a consistent state: moving it back swaps again.
            string back = Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B3", "--node", "DCS:" + ApacheWarning + ":" + Apaches,
                "--device", Panel, "--input", "JOY_BTN1", "--dry-run");
            Assert.Contains("\"TakenRelation\":\"Button CP2 B1\"", back);
            Assert.Contains("Master Caution", back);
        }

        // Replace: the action on BTN 3 leaves it (for the moved aircraft only); keep both: a relation of its own on BTN 3.
        using (TestFolder folder = new())
        {
            string profile = folder.Profile();
            BuildB1B3(engine, folder, profile);
            Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + ApacheWarning + ":AH-64D_BLK_II_PLT",
                "--device", Panel, "--input", "JOY_BTN3", "--if-taken", "replace");
            string b3 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B3", "--text");
            Assert.Contains(ApacheCaution + ", aircraft=AH-64D_BLK_II_CPG", b3);
            Assert.DoesNotContain(ApacheCaution + ", aircraft=AH-64D_BLK_II_CPG, AH-64D_BLK_II_PLT", b3);
            Assert.Contains(ApacheWarning + ", aircraft=AH-64D_BLK_II_PLT", b3);

            Engine(engine, folder, "--profile", profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + ApacheWarning + ":AH-64D_BLK_II_CPG",
                "--device", Panel, "--input", "JOY_BTN3", "--if-taken", "keep-both");
            string list = Engine(engine, folder, "--profile", profile, "relations", "list", "--text");
            Assert.Contains("JOY_BTN3", list.Split('\n').FirstOrDefault(l => l.StartsWith("Master Warning", StringComparison.Ordinal)) ?? string.Empty);
        }
    }

    // The panel: right-click the AH-64D's action in B1, press BTN 3 (a fake device that spells the GUID differently): the
    // panel names B3 and its action, Move waits for a choice; swap moves both. The editor stays on B1 meanwhile.
    [Fact]
    public async Task MovePanel_RealEngine_RecordOnTakenButton_OffersChoices_AndSwaps()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        using TestFolder folder = new();
        string profile = folder.Profile();
        BuildB1B3(engine, folder, profile);
        FakeCapture capture = new() { Next = new JoystickCaptureService.CaptureResult { Device = PanelOtherCase, Input = "JOY_BTN3" } };
        MainWindowViewModel vm = folder.Vm(engine, profile, capture);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Button CP2 B1");
        await vm.ShowRelationAsync("Button CP2 B1");
        RelationNodeItem apache = vm.RelationNodes.Single(n => n.Id == ApacheWarning);

        vm.OpenMoveActionForNodeCommand.Execute(apache);
        Assert.True(vm.IsMoveActionOpen);
        Assert.Contains("'Button CP2 B1'", vm.MoveActionIntro);
        if (HasDcsLists(engine))
            Assert.Contains("Master Warning", vm.MoveActionTitle);

        await vm.RecordMoveActionInputCommand.ExecuteAsync(null);
        Assert.Equal("JOY_BTN3", vm.MoveActionInput);
        Assert.True(OverlayResolver.SameDevice(Panel, vm.MoveActionDevice), vm.MoveActionDevice);
        Assert.Equal(Panel, capture.LastOptions?.PreferredDevice);
        await WaitFor(() => vm.MoveActionTaken);
        Assert.Contains("Button CP2 B3", vm.MoveActionTakenText);
        Assert.Contains("AH-64D", vm.MoveActionTakenText);
        if (HasDcsLists(engine))
            Assert.Contains("Master Caution", vm.MoveActionTakenText);
        Assert.DoesNotContain("No relation uses", vm.MoveActionPreview);
        Assert.False(vm.CanApplyMoveAction);
        Assert.Contains("Button CP2 B1", vm.MoveActionSwapText);

        vm.IsMoveActionSwap = true;
        await WaitFor(() => vm.MoveActionPreview.Length > 0);
        Assert.Contains("Button CP2 B3", vm.MoveActionPreview);
        Assert.Contains("Button CP2 B1", vm.MoveActionPreview);
        Assert.True(vm.CanApplyMoveAction);
        // The editor still shows B1 (follow input is off while the panel is open).
        Assert.Equal("Button CP2 B1", vm.SelectedRelation?.Name);

        await vm.ApplyMoveActionCommand.ExecuteAsync(null);
        Assert.False(vm.IsMoveActionOpen);
        string b1 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B1", "--text");
        string b3 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B3", "--text");
        Assert.Contains(ApacheCaution, b1);
        Assert.Contains(ApacheWarning, b3);
        Assert.Equal(3, vm.Relations.Count);
        // The editor shows the new state at once: B1 (where it came from) has the AH-64D's Master Caution now.
        Assert.Equal("Button CP2 B1", vm.SelectedRelation?.Name);
        Assert.Contains(vm.RelationNodes, n => n.Id == ApacheCaution);
        Assert.DoesNotContain(vm.RelationNodes, n => n.Id == ApacheWarning);
    }

    // Search "master": the cards show the matching actions with game and aircraft; "master caution" puts the relations
    // with that phrase in an action first; a click opens the relation at that action. The same on the Binds page.
    [Fact]
    public async Task Search_RealEngine_ShowsWhatMatched_RanksThePhrase_AndOpensTheAction()
    {
        string? engine = FindEngine();
        if (engine == null || !HasDcsLists(engine))
            return;
        using TestFolder folder = new();
        string profile = folder.Profile();
        BuildB1B3(engine, folder, profile);
        MainWindowViewModel vm = folder.Vm(engine, profile, new FakeCapture());
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;

        vm.RelationSearchText = "master";
        Assert.Equal(3, vm.Relations.Count);
        RelationListItem b1 = vm.Relations.Single(r => r.Name == "Button CP2 B1");
        Assert.True(b1.SearchHits.HasLines);
        Assert.All(b1.SearchHits.Lines, l => Assert.Equal("master", l.Hit, ignoreCase: true));
        SearchMatchLine warning = b1.SearchHits.Lines.Single(l => l.Title == "Master Warning Button");
        Assert.Contains("DCS", warning.Where);
        Assert.Contains("AH-64D", warning.Where);
        Assert.Contains("(2)", warning.Where);
        RelationListItem b2 = vm.Relations.Single(r => r.Name == "Button CP2 B2");
        Assert.Contains(b2.SearchHits.Lines, l => l.Where.Contains("2024", StringComparison.Ordinal));

        // The phrase: B1 (F-16C "Master Caution Button") and B3 (AH-64D "Master Caution Button") before B2.
        vm.RelationSearchText = "master caution";
        Assert.Equal(["Button CP2 B1", "Button CP2 B3"], vm.Relations.Take(2).Select(r => r.Name).OrderBy(n => n).ToList());
        RelationListItem b3 = vm.Relations.Single(r => r.Name == "Button CP2 B3");
        SearchMatchLine caution = b3.SearchHits.Lines.First();
        Assert.Equal("Master Caution", caution.Hit);

        await vm.OpenSearchMatchCommand.ExecuteAsync(caution);
        Assert.Equal("Button CP2 B3", vm.SelectedRelation?.Name);
        Assert.Equal(ApacheCaution, vm.SelectedRelationNode?.Id);
        Assert.True(vm.SelectedRelationNode?.IsSearchHit);
        Assert.Equal("master caution", vm.RelationSearchText);

        // No search: no lines.
        vm.RelationSearchText = string.Empty;
        Assert.All(vm.Relations, r => Assert.False(r.SearchHits.HasLines));

        // The Binds page has its own search and lines; a click marks the action in the peek.
        vm.CurrentStage = UiStage.Binds;
        vm.BindSearchText = "warning";
        BindListItem row = vm.Binds.Single(b => b.RelationName == "Button CP2 B1");
        SearchMatchLine hit = Assert.Single(row.SearchHits.Lines);
        Assert.Equal("Warning", hit.Hit);
        await vm.OpenSearchMatchCommand.ExecuteAsync(hit);
        Assert.Equal("Button CP2 B1", vm.SelectedBind?.RelationName);
        Assert.Contains(vm.BindPeekGroups.SelectMany(g => g.Actions), a => a.IsSearchHit && a.Id == ApacheWarning);
    }

    // More than three matching actions: three lines, then "+N more".
    [Fact]
    public void MatchLines_KeepTheCardCompact()
    {
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new FakeCapture(), loadPersistedUiSettings: false);
        RelationListItem relation = new()
        {
            Name = "Button CP2 B1",
            ActionTitleEntries = Enumerable.Range(0, 6)
                .Select(i => new ActionTitleEntry("Master Caution " + i, "DCS", i, "id" + i, ["F-16C_50"]))
                .Append(new ActionTitleEntry("Gear", "DCS", 7, "gear", ["F-16C_50"]))
                .Append(new ActionTitleEntry("Master Warning", "MSFS2024", 8, "KEY_MASTER_WARNING_TOGGLE", []))
                .ToList()
        };
        List<SearchMatchLine> lines = vm.BuildSearchMatchLines(relation, ["master", "warning"], out int more);
        Assert.Equal(MainWindowViewModel.MaxSearchMatchLines, lines.Count);
        Assert.Equal(4, more);
        // The title with both words first.
        Assert.Equal("Master Warning", lines[0].Title);
        Assert.Equal("Master Warning", lines[0].Hit);
        Assert.Equal(("Gear ", "master", " lever"), MainWindowViewModel.MarkSearchHit("Gear master lever", ["master"], "master"));
    }

    // 5000 relations with 10 titles each: typing a search stays within the list's budget.
    [Fact]
    public void MatchLines_5000Relations_StayFast()
    {
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new FakeCapture(), loadPersistedUiSettings: false);
        List<RelationListItem> relations = Enumerable.Range(0, 5000).Select(i => new RelationListItem
        {
            Name = "Relation " + i,
            ActionTitleEntries = Enumerable.Range(0, 10).Select(j => new ActionTitleEntry((j % 3 == 0 ? "Master Caution " : "Gear ") + j, "DCS", j, "id" + j, ["F-16C_50", "FA-18C_hornet"])).ToList()
        }).ToList();
        vm.BuildSearchMatchLines(relations[0], ["master"], out _);
        Stopwatch watch = Stopwatch.StartNew();
        foreach (RelationListItem relation in relations)
            relation.SearchHits.Set(vm.BuildSearchMatchLines(relation, ["master", "caution"], out int more), more);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 400, watch.ElapsedMilliseconds + " ms");
    }

    // The search with its match lines on Relations and Binds, and the Move panel on a taken button, at 2267 and 1280 in
    // light and dark, and at 150 % / 200 % text: no overlaps, nothing sticking out, no cut text (LayoutChecks, gate I10).
    // PNGs to JOYPRO_SEARCHHIT_OUT, else %TEMP%\JoyPro2.Gui.Tests\search-hits.
    [Fact]
    public async Task SearchHitsAndTakenPanel_Render_WithoutLayoutProblems()
    {
        string? engine = FindEngine();
        if (engine == null || !HasDcsLists(engine))
            return;
        string outDir = Environment.GetEnvironmentVariable("JOYPRO_SEARCHHIT_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "search-hits");
        Directory.CreateDirectory(outDir);
        using TestFolder folder = new();
        string profile = folder.Profile();
        BuildB1B3(engine, folder, profile);
        MainWindowViewModel vm = folder.Vm(engine, profile, new FakeCapture());
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.RelationSearchText = "master";
        vm.BindSearchText = "master";
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Button CP2 B1");
        await vm.ShowRelationAsync("Button CP2 B1");
        List<string> problems = [];

        void Shoot(string name, double scale, IEnumerable<(int Width, int Height)> sizes, string? scopeName = null)
        {
            AvaloniaRenderThread.Run(() =>
            {
                ThemeVariant? before = Application.Current!.RequestedThemeVariant;
                try
                {
                    ThemeService.ApplyTypography(scale, null);
                    foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                    {
                        Application.Current!.RequestedThemeVariant = variant;
                        foreach ((int width, int height) in sizes)
                        {
                            using HiddenWindow window = GateRender.NewWindow(vm, width, height);
                            for (int i = 0; i < 8; i++)
                            {
                                window.Settle();
                                Thread.Sleep(30);
                            }
                            Control root = GateRender.Root(window);
                            // The Move panel lies over the page on purpose: only the panel itself is checked.
                            Control? scope = scopeName == null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Control>().FirstOrDefault(c => c.Name == scopeName);
                            Assert.True(scopeName == null || scope != null, scopeName + " not found");
                            void Check(string suffix)
                            {
                                string where = name + suffix + " " + theme + " " + width + "x" + height + " text " + (int)(scale * 100) + "%";
                                List<Rect> boxes = [];
                                if (name.Contains("search", StringComparison.Ordinal))
                                {
                                    List<Button> hits = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Button>().Where(x => x.Classes.Contains("searchHit") && x.IsEffectivelyVisible).ToList();
                                    if (hits.Count == 0 || hits.Any(x => !x.IsEffectivelyEnabled || x.Command == null))
                                        problems.Add(where + ": " + hits.Count + " match line(s), some without their command");
                                }
                                foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height, scope))
                                {
                                    problems.Add(where + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b));
                                    boxes.Add(ra);
                                    boxes.Add(rb);
                                }
                                foreach ((Control item, Rect ir, Border frame, Rect _) in LayoutChecks.Spills(root, width, height, scope))
                                {
                                    problems.Add(where + ": " + LayoutChecks.Describe(item) + " sticks out of " + GateRender.Describe(frame));
                                    boxes.Add(ir);
                                }
                                foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, scope))
                                    problems.Add(where + ": '" + Gate.Short(text.Text ?? string.Empty, 50) + "' cut (needs " + need.ToString("0", CultureInfo.InvariantCulture) + ", has " + got.ToString("0", CultureInfo.InvariantCulture) + ")");
                                GateRender.Save(window, width, height, 1, Path.Combine(outDir, name + suffix + "-" + theme + "-" + width + "-text" + (int)(scale * 100) + ".png"), boxes);
                            }
                            Check(string.Empty);
                            // At a big text size the page scrolls as a whole: the lower part (the cards, the Move panel's buttons) too.
                            if (scale > 1.0 && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<ScrollViewer>().FirstOrDefault(v => v.Name == "PageScroller") is { } page && page.Extent.Height > page.Viewport.Height + 1)
                            {
                                page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
                                for (int i = 0; i < 6; i++)
                                {
                                    window.Settle();
                                    Thread.Sleep(30);
                                }
                                Check("-scrolled");
                            }
                        }
                    }
                }
                finally
                {
                    ThemeService.ApplyTypography(1.0, null);
                    Application.Current!.RequestedThemeVariant = before;
                }
            });
        }

        vm.CurrentStage = UiStage.Relations;
        await Quiet(vm);
        Shoot("relations-search", 1.0, [(2267, 1250), (1280, 800)]);
        Shoot("relations-search", 1.5, [(1280, 800)]);
        Shoot("relations-search", 2.0, [(1280, 800)]);
        vm.CurrentStage = UiStage.Binds;
        await Quiet(vm);
        Shoot("binds-search", 1.0, [(2267, 1250), (1280, 800)]);
        Shoot("binds-search", 2.0, [(1280, 800)]);

        // The Move panel on the taken button with its choices.
        vm.CurrentStage = UiStage.Relations;
        vm.RelationSearchText = string.Empty;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Button CP2 B1");
        await vm.ShowRelationAsync("Button CP2 B1");
        vm.OpenMoveActionForNodeCommand.Execute(vm.RelationNodes.Single(n => n.Id == ApacheWarning));
        vm.MoveActionDevice = Panel;
        vm.MoveActionInput = "JOY_BTN3";
        await WaitFor(() => vm.MoveActionTaken);
        await Quiet(vm);
        Shoot("move-taken", 1.0, [(2267, 1250), (1280, 800)], "MoveActionPanel");
        Shoot("move-taken", 2.0, [(1280, 800)], "MoveActionPanel");
        vm.IsMoveActionSwap = true;
        await WaitFor(() => vm.MoveActionPreview.Length > 0);
        await Quiet(vm);
        Shoot("move-taken-swap", 1.0, [(1280, 800)], "MoveActionPanel");
        vm.CancelMoveActionCommand.Execute(null);

        File.WriteAllText(Path.Combine(outDir, "report.txt"), string.Join(Environment.NewLine, problems));
        List<string> distinct = problems.Distinct().ToList();
        Assert.True(distinct.Count == 0, distinct.Count + " layout problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, distinct.Take(40)));
    }

    // The view model's background loads (a page switch, a preview) finish before a render reads it on the render thread.
    private static async Task Quiet(MainWindowViewModel vm)
    {
        for (int quiet = 0, i = 0; quiet < 5 && i < 100; i++)
        {
            await Task.Delay(100);
            quiet = vm.IsBusy ? 0 : quiet + 1;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 150 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition(), "timed out");
    }

    private static bool HasDcsLists(string engine) => Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS"));

    private static string? FindEngine()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_TEST_ENGINE") is { Length: > 0 } other)
            return File.Exists(other) ? other : null;
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        string? path = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return path != null && File.Exists(path) ? path : null;
    }

    private static string Engine(string engine, TestFolder folder, params string[] args)
    {
        (int exit, string output) = RunEngine(engine, folder, args);
        Assert.True(exit == 0, "engine " + string.Join(" ", args) + " failed: " + output);
        return output;
    }

    private static int EngineExit(string engine, TestFolder folder, params string[] args) => RunEngine(engine, folder, args).Exit;

    private static (int Exit, string Output) RunEngine(string engine, TestFolder folder, string[] args)
    {
        ProcessStartInfo info = new(engine) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = folder.Data;
        info.Environment["JOYPRO_ENGINE_SERVER"] = "off";
        info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    // A device that "presses" the given input once.
    private sealed class FakeCapture : IJoystickCaptureService
    {
        public JoystickCaptureService.CaptureResult? Next { get; set; }

        public JoystickCaptureService.CaptureOptions? LastOptions { get; private set; }

        public string LastDiagnostics => string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            JoystickCaptureService.CaptureResult? next = Next;
            Next = null;
            return Task.FromResult(next);
        }

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();

        public TestFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "move-taken-" + Guid.NewGuid().ToString("N"));
            Data = Path.Combine(Root, "data");
            Directory.CreateDirectory(Data);
            foreach ((string name, string value) in new[] { ("JOYPRO_DATA_DIR", Data), ("JOYPRO_ENGINE_SERVER", "off"), ("JOYPRO_TEST_IGNORE_RUNNING_GAMES", "1") })
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public string Root { get; }

        public string Data { get; }

        public string Profile() => Path.Combine(Root, "test.pr0file");

        public MainWindowViewModel Vm(string engine, string profile, IJoystickCaptureService capture) =>
            new(new EngineCliClient(), capture, loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
