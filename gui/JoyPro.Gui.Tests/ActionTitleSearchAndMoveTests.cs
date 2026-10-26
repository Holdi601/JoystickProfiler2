using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tester reports on 2.4.1 (real engine, a profile built with engine commands as an import or a take-over leaves it:
// its actions have no titles stored with them):
//   - Search "Master" on Relations and Binds found nothing although "Toggle Master Warning" (MSFS 2024) is an action;
//     none of the DCS action titles were found either: engine relations list --titles only read titles stored with
//     the nodes, and such a profile has none. Now the titles come from the game lists.
//   - "I simply want to change the 'Toggle Master Warning' from one button to another": Move to another button moves
//     one action into the relation on the new input (or a new relation), the other actions stay.
// Runs when the engine is built (engine\bin\x64\<configuration>\engine.exe); never shows a window.
[Collection("ProcessEnvironment")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ActionTitleSearchAndMoveTests
{
    private const string Panel = "Test Panel #2 {03725130-D2C1-11F0-8003-444553540000}";
    private const string MsfsNode = "MSFS2024:KEY_MASTER_WARNING_TOGGLE";
    private const string DcsStickNode = "DCS:d3024pnilunilcd10vd1vpnilvunil:F-16C_50";

    [Fact]
    public async Task TitleSearch_RealEngine_FindsMsfsAndDcsActionTitles_OnRelationsAndBinds()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        using TestFolder folder = new();
        string profile = folder.Profile();
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B2", "--button", "--node", DcsStickNode, "--node", MsfsNode, "--device", Panel, "--input", "JOY_BTN2");
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B1", "--button", "--node", "MSFS2024:KEY_PARKING_BRAKES", "--device", Panel, "--input", "JOY_BTN1");

        MainWindowViewModel vm = folder.Vm(engine, profile);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Relations.Count);
        RelationListItem b2 = vm.Relations.Single(r => r.Name == "Button CP2 B2");
        Assert.Contains(b2.ActionTitles, t => t.Contains("Master Warning", StringComparison.OrdinalIgnoreCase));

        foreach (string word in new[] { "master", "warning", "toggle master", "Master" })
        {
            vm.RelationSearchText = word;
            Assert.Equal(["Button CP2 B2"], vm.Relations.Select(r => r.Name).ToList());
            vm.BindSearchText = word;
            Assert.Equal(["Button CP2 B2"], vm.Binds.Select(b => b.RelationName).Distinct().ToList());
        }
        vm.RelationFilter.SearchScope = ListSearchScope.Everything;
        vm.RelationSearchText = "master";
        Assert.Contains(vm.Relations, r => r.Name == "Button CP2 B2");
        vm.RelationFilter.SearchScope = ListSearchScope.NameAndActions;

        // DCS titles come from the DCS list (only in a checkout with the development game lists).
        if (Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")))
        {
            Assert.Contains(b2.ActionTitles, t => t.Contains("Stick", StringComparison.OrdinalIgnoreCase));
            vm.RelationSearchText = "hide";
            Assert.Contains(vm.Relations, r => r.Name == "Button CP2 B2");
            vm.BindSearchText = "hide";
            Assert.Contains(vm.Binds, b => b.RelationName == "Button CP2 B2");
        }
    }

    // Opt-in, a tester's restored profile: JOYPRO_MASTER_PROFILE = the profile (a copy is opened), JOYPRO_MASTER_WORD
    // (default "master") and JOYPRO_MASTER_RELATION (default "Button CP2 B2", the relation that must be found).
    [Fact]
    public async Task TitleSearch_RealEngine_TesterProfile_FindsTheRelationByItsActions()
    {
        string? engine = FindEngine();
        string? source = Environment.GetEnvironmentVariable("JOYPRO_MASTER_PROFILE");
        if (engine == null || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        string word = Environment.GetEnvironmentVariable("JOYPRO_MASTER_WORD") is { Length: > 0 } w ? w : "master";
        string expected = Environment.GetEnvironmentVariable("JOYPRO_MASTER_RELATION") is { Length: > 0 } r ? r : "Button CP2 B2";
        using TestFolder folder = new();
        string profile = folder.Profile();
        File.Copy(source, profile, overwrite: true);
        MainWindowViewModel vm = folder.Vm(engine, profile);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        Assert.True(vm.Relations.Count > 50, "the profile did not load: " + vm.StatusText);
        // Most relations have titles now (2.4.1: none of 382).
        Assert.True(vm.Relations.Count(x => x.ActionTitles.Count > 0) > vm.Relations.Count * 9 / 10,
            vm.Relations.Count(x => x.ActionTitles.Count > 0) + " of " + vm.Relations.Count + " relations have action titles");
        vm.RelationSearchText = word;
        Assert.Contains(vm.Relations, x => x.Name == expected);
        vm.BindSearchText = word;
        Assert.Contains(vm.Binds, b => b.RelationName == expected);
    }

    [Fact]
    public async Task MoveToAnotherButton_RealEngine_JoinsTheRelationOnThatButton_OrMakesOne()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        using TestFolder folder = new();
        string profile = folder.Profile();
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B2", "--button", "--node", DcsStickNode, "--node", MsfsNode, "--device", Panel, "--input", "JOY_BTN2");
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B3", "--button", "--node", "DCS:d3001pnilu3001cd6vd1vpnilvu0:F-5E-3", "--device", Panel, "--input", "JOY_BTN3");

        MainWindowViewModel vm = folder.Vm(engine, profile);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);

        // From the Binds page's bind row: every action of the relation to pick from.
        BindListItem row = vm.Binds.Single(b => b.RelationName == "Button CP2 B2");
        vm.SelectedBind = row;
        await vm.OpenMoveActionForBindCommand.ExecuteAsync(row);
        Assert.True(vm.IsMoveActionOpen);
        Assert.Equal(2, vm.MoveActionChoices.Count);
        Assert.True(vm.ShowMoveActionPicker);
        vm.SelectedMoveAction = vm.MoveActionChoices.Single(c => c.Game == "MSFS2024");
        Assert.Contains("Master Warning", vm.SelectedMoveAction.Title, StringComparison.OrdinalIgnoreCase);
        vm.MoveActionDevice = Panel;
        vm.MoveActionInput = "JOY_BTN3";
        Assert.True(vm.CanApplyMoveAction);
        await vm.ApplyMoveActionCommand.ExecuteAsync(null);
        Assert.False(vm.IsMoveActionOpen);
        Assert.Contains("Button CP2 B3", vm.StatusText);

        string b3 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B3", "--text");
        string b2 = Engine(engine, folder, "--profile", profile, "relations", "show", "Button CP2 B2", "--text");
        Assert.Contains("KEY_MASTER_WARNING_TOGGLE", b3);
        Assert.DoesNotContain("KEY_MASTER_WARNING_TOGGLE", b2);
        Assert.Contains("d3024pnilunilcd10vd1vpnilvunil", b2);
        Assert.Equal(2, vm.Relations.Count);

        // On a button no relation uses: a relation of its own, named after the action; one undo step.
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Button CP2 B3");
        await vm.OpenMoveActionForBindCommand.ExecuteAsync(vm.SelectedBind);
        vm.SelectedMoveAction = vm.MoveActionChoices.Single(c => c.Game == "MSFS2024");
        vm.MoveActionDevice = Panel;
        vm.MoveActionInput = "JOY_BTN9";
        await vm.ApplyMoveActionCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Relations.Count);
        RelationListItem moved = vm.Relations.Single(r => r.Name != "Button CP2 B2" && r.Name != "Button CP2 B3");
        Assert.Contains("Master Warning", moved.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("JOY_BTN9", moved.BindInputRaw);
        string history = Engine(engine, folder, "--profile", profile, "profile", "history", "list", "--text");
        Assert.Contains("Moving '", history);
    }

    // The Move panel over the Binds page at 2267 and 1280 in light and dark, rendered offscreen (never shown), with the
    // engine's answer where the action goes. PNGs to JOYPRO_MOVE_OUT, else %TEMP%\JoyPro2.Gui.Tests\move-action.
    [Fact]
    public async Task MoveToAnotherButton_Panel_Renders_InBothThemes()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        string outDir = Environment.GetEnvironmentVariable("JOYPRO_MOVE_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "move-action");
        Directory.CreateDirectory(outDir);
        using TestFolder folder = new();
        string profile = folder.Profile();
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B2", "--button", "--node", DcsStickNode, "--node", MsfsNode, "--device", Panel, "--input", "JOY_BTN2");
        Engine(engine, folder, "--profile", profile, "relations", "add", "Button CP2 B3", "--button", "--node", "DCS:d3001pnilu3001cd6vd1vpnilvu0:F-5E-3", "--device", Panel, "--input", "JOY_BTN3");
        MainWindowViewModel vm = folder.Vm(engine, profile);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Binds;
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Button CP2 B2");
        await vm.OpenMoveActionForBindCommand.ExecuteAsync(vm.SelectedBind);
        vm.SelectedMoveAction = vm.MoveActionChoices.Single(c => c.Game == "MSFS2024");
        vm.MoveActionDevice = Panel;
        vm.MoveActionInput = "JOY_BTN3";
        for (int i = 0; i < 100 && !vm.HasMoveActionPreview; i++)
            await Task.Delay(100);
        Assert.Contains("Button CP2 B3", vm.MoveActionPreview);

        AvaloniaRenderThread.Run(() =>
        {
            Avalonia.Styling.ThemeVariant? before = Avalonia.Application.Current!.RequestedThemeVariant;
            try
            {
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in new[] { ("light", Avalonia.Styling.ThemeVariant.Light), ("dark", Avalonia.Styling.ThemeVariant.Dark) })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
                    {
                        using HiddenWindow host = HiddenWindow.Create(new JoyPro.Gui.Views.MainWindow { DataContext = vm }, new Avalonia.Size(width, height));
                        host.Settle();
                        host.Settle();
                        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = host.Render();
                        bitmap.Save(Path.Combine(outDir, "move-action-" + theme + "-" + width + ".png"));
                    }
                }
            }
            finally
            {
                Avalonia.Application.Current!.RequestedThemeVariant = before;
            }
        });
        vm.CancelMoveActionCommand.Execute(null);
        Assert.False(vm.IsMoveActionOpen);
    }

    // JOYPRO_TEST_ENGINE: another engine.exe (e.g. a released one, to see the tests fail on the old engine).
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
        ProcessStartInfo info = new(engine) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = folder.Data;
        info.Environment["JOYPRO_ENGINE_SERVER"] = "off";
        info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "engine " + string.Join(" ", args) + " failed: " + output);
        return output;
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();

        public TestFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "titles-move-" + Guid.NewGuid().ToString("N"));
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

        public MainWindowViewModel Vm(string engine, string profile) =>
            new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
