using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The quick path's "Write to my game" (third novice review B1, docs/export.md "Write to my game"): an empty game asks
// nothing; a game that already has stick buttons asks ONE plain question (the new ones are the main answer, keeping
// the old ones writes with "Only add" for that write only, Cancel writes nothing and says how to go on); the Export
// page keeps its own questions. Every test answers through an explicit recording handler, so no question is ever
// answered "yes" silently, also not across a window that attaches to the view model and lets go of it again.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class QuickWriteQuestionTests : IDisposable
{
    private const string Il2 = "IL2Game";

    public void Dispose() => Loc.SetLanguage("en");

    private static string State(bool imported = false, bool exported = false, int stickBinds = 0, bool read = true) =>
        "{\"Game\":\"IL2Game\",\"ConfigPath\":\"" + (read || imported || exported ? "C:\\\\IL-2\\\\data\\\\input\\\\current.map" : string.Empty) + "\",\"Imported\":" + Json(imported) +
        ",\"Exported\":" + Json(exported) + ",\"GameHasBinds\":" + Json(stickBinds > 0) + ",\"GameBindCount\":" + (stickBinds + 40) +
        ",\"GameJoystickBindCount\":" + stickBinds + ",\"GameFilesRead\":" + Json(read && !imported && !exported) + "}";

    private static string Json(bool value) => value ? "true" : "false";

    // A new profile with IL-2 (the first start's game setup), the questions recorded; answer decides each one.
    private static async Task<(MainWindowViewModel Vm, QuickSetupPersonaTests.PersonaEngine Engine, List<ConfirmRequest> Asked)> StartAsync(
        string gameState, Func<ConfirmRequest, bool> answer)
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("ursa-minor.tsv");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        engine.GameStateJson = gameState;
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine);
        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = request =>
        {
            asked.Add(request);
            return Task.FromResult(answer(request));
        };
        return (vm, engine, asked);
    }

    private static IReadOnlyList<string>? Il2Export(QuickSetupPersonaTests.PersonaEngine engine) =>
        engine.Calls.LastOrDefault(c => NoviceGuiTests.Seq(c, "il2", "export"));

    private static string ArgAfter(IReadOnlyList<string> args, string name)
    {
        int i = args.ToList().IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : string.Empty;
    }

    [Fact]
    public async Task EmptyGame_AsksNothing_AndWrites()
    {
        (MainWindowViewModel vm, QuickSetupPersonaTests.PersonaEngine engine, List<ConfirmRequest> asked) = await StartAsync(State(read: false), _ => true);
        await vm.QuickExportAsync([Il2]);
        Assert.Empty(asked);
        Assert.Empty(vm.AskedConfirmations);
        IReadOnlyList<string> export = Il2Export(engine)!;
        Assert.NotNull(export);
        Assert.Equal("clean", ArgAfter(export, "--mode"));
        Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task GameWithStickButtons_AsksOnePlainQuestion_UseTheNewOnes()
    {
        (MainWindowViewModel vm, QuickSetupPersonaTests.PersonaEngine engine, List<ConfirmRequest> asked) = await StartAsync(State(stickBinds: 12), _ => true);
        await vm.QuickExportAsync([Il2]);
        ConfirmRequest question = Assert.Single(asked);
        Assert.Equal("IL-2 Sturmovik: Great Battles already has buttons set", question.Title);
        Assert.Equal("IL-2 Sturmovik: Great Battles already has buttons set. Use the new ones instead? JoyPro keeps a copy of the old ones, and you can undo.", question.Message);
        // The new ones are the main answer, keeping the old ones the second; no warning sign, no "remove", no "binds".
        Assert.Equal("Use the new ones", question.ConfirmLabel);
        Assert.Equal("Keep the old ones too", question.SecondaryLabel);
        Assert.False(question.SecondaryIsPrimary);
        Assert.False(question.IsDanger);
        Assert.DoesNotContain("remove", question.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bind", question.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vm.AskedConfirmations, r => r.Title.StartsWith("Remove binds", StringComparison.Ordinal) || r.Title.StartsWith("First export", StringComparison.Ordinal));
        Assert.Equal("clean", ArgAfter(Il2Export(engine)!, "--mode"));
        Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task KeepTheOldOnes_WritesWithOnlyAdd_ForThisWriteOnly()
    {
        (MainWindowViewModel vm, QuickSetupPersonaTests.PersonaEngine engine, List<ConfirmRequest> asked) = await StartAsync(State(stickBinds: 12), request =>
        {
            request.SecondaryChosen = true;
            return true;
        });
        string modeBefore = vm.ExportMode;
        bool nukeBefore = vm.ExportNukeUntouchedDevices;
        await vm.QuickExportAsync([Il2]);
        Assert.Single(asked);
        IReadOnlyList<string> export = Il2Export(engine)!;
        // "Only add" never removes a game bind, and nothing clears the other devices.
        Assert.Equal("add", ArgAfter(export, "--mode"));
        Assert.Equal("false", ArgAfter(export, "--nuke-untouched-devices"));
        Assert.Equal(modeBefore, vm.ExportMode);
        Assert.Equal(nukeBefore, vm.ExportNukeUntouchedDevices);
        Assert.Contains("your old ones stayed", vm.StatusText);
        // It goes on: the quick tour starts as after the other answer.
        Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task Cancel_WritesNothing_AndSaysHowToGoOn()
    {
        (MainWindowViewModel vm, QuickSetupPersonaTests.PersonaEngine engine, List<ConfirmRequest> asked) = await StartAsync(State(stickBinds: 3), _ => false);
        await vm.QuickExportAsync([Il2]);
        Assert.Single(asked);
        Assert.Null(Il2Export(engine));
        Assert.Null(vm.ActiveTour);
        Assert.Equal("Nothing was written yet. When you are ready, press Write to my game again.", vm.StatusText);
    }

    [Fact]
    public async Task GameThisProfileImportedOrWrote_AsksToo()
    {
        foreach (string state in new[] { State(imported: true, read: false), State(exported: true, read: false), "not json" })
        {
            (MainWindowViewModel vm, _, List<ConfirmRequest> asked) = await StartAsync(state, _ => true);
            await vm.QuickExportAsync([Il2]);
            Assert.Single(asked);
            vm.EndTourCommand.Execute(null);
        }
    }

    [Fact]
    public async Task ExportPage_KeepsItsOwnQuestion()
    {
        // The Export page's "Export ticked games" with the default clean + nuke still asks whether binds may be removed
        // (docs/export.md): only the quick path's click counts as the answer.
        (MainWindowViewModel vm, QuickSetupPersonaTests.PersonaEngine engine, List<ConfirmRequest> asked) = await StartAsync(State(read: false), _ => true);
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        foreach (JoyPro.Gui.Models.ExportGameItem tile in vm.ExportGames)
            tile.IsSelected = tile.GameId == Il2;
        await vm.ExportSelectedGamesCommand.ExecuteAsync(null);
        ConfirmRequest question = Assert.Single(asked);
        Assert.Equal("Remove binds in IL-2 Sturmovik: Great Battles?", question.Title);
        Assert.True(question.IsDanger);
        // Joined in the language's words: "A and B", never an English " and " in another language.
        Assert.Contains("(the game's defaults too) and all binds of the devices", question.Message);
        Assert.NotNull(Il2Export(engine));
    }

    [Fact]
    public void ListJoin_IsInTheLanguageShown()
    {
        Loc.SetLanguage("de");
        Assert.Equal("A, B und C", Loc.JoinAnd(["A", "B", "C"]));
        Assert.Equal("A oder B", Loc.JoinOr(["A", "B"]));
        Assert.Equal("A", Loc.JoinAnd(["A"]));
        // The removal question in German: no English "and", Sie throughout.
        string message = Loc.T("This export removes {0}, only in the aircraft you export. JoyPro backs up the game files first; \"Undo last export\" on the game's tile puts them back.",
            Loc.JoinAnd([Loc.T("every bind on the devices of your profile that is not in your profile (the game's defaults too)"), Loc.T("all binds of the devices your profile does not use")]));
        Assert.DoesNotContain(" and ", message);
        Assert.DoesNotContain(" du ", message);
        Assert.Contains(" und ", message);
        Assert.Contains("die Sie exportieren", message);
    }

    [Fact]
    public void AWindow_NeverTakesAwayTheTestsAnswer()
    {
        // MainWindow answers questions with its dialog only when nobody else does; when it lets go of the view model, the
        // handler before it comes back (it used to set null, and null answers "yes").
        List<ConfirmRequest> asked = [];
        Func<ConfirmRequest, Task<bool>> recording = request =>
        {
            asked.Add(request);
            return Task.FromResult(false);
        };
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { ConfirmHandler = recording };
            JoyPro.Gui.Views.MainWindow window = new() { DataContext = vm };
            Assert.Same(recording, vm.ConfirmHandler);
            window.DataContext = null;
            Assert.Same(recording, vm.ConfirmHandler);

            // Without a handler of its own the window answers, and gives it back on detach.
            MainWindowViewModel bare = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
            JoyPro.Gui.Views.MainWindow other = new() { DataContext = bare };
            Assert.NotNull(bare.ConfirmHandler);
            other.DataContext = null;
            Assert.Null(bare.ConfirmHandler);
            vm.StopGameConfigWatcher();
            bare.StopGameConfigWatcher();
        });
        Assert.Empty(asked);
    }
}
