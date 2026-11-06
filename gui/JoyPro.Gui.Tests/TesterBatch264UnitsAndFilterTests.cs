using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The second 2.6.3 tester batch: axis response numbers in DCS's own units ("just entering 8"), the relation filter "No
// action in Falcon BMS" (a tester adding BMS to one device's relations compared lists by eye), the explanation of detent
// mode, and the search that said it found nothing while the actions were in the relation.
// Pictures with JOYPRO_264C_RENDER_OUT=<folder> (dark and light).
[SupportedOSPlatform("windows")]
public sealed class AxisUnitsTests
{
    private static AxisParameterEditor Editor(MainWindowViewModel vm, string key) => vm.AxisTuningEditors.Single(e => e.Key == key);

    private static string After(IReadOnlyList<string> args, string name) => args[args.ToList().IndexOf(name) + 1];

    private static void Key(TextBox box, Key key) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = box });

    // Typing 8 is 8, as in DCS's dialog; the file, the engine and everything exported keep 0.08.
    [Fact]
    public async Task Dcs_BoxesShowItsDialogsUnits_WhatIsStoredStaysAsItWas()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake);
        await AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50");

        AxisParameterEditor dead = Editor(vm, "deadzone");
        Assert.Equal("0 .. 100", dead.RangeText);
        Assert.Equal((0.0, 100.0, 1.0), (dead.BoxMinimum, dead.BoxMaximum, dead.BoxStep));
        Assert.Equal("0 .. 100", Editor(vm, "saturationX").RangeText);
        Assert.Equal("0 .. 100", Editor(vm, "saturationY").RangeText);
        // The dialog's curvature slider runs -1..1 and its box shows value * 100.
        Assert.Equal("-100 .. 100", Editor(vm, "curvature").RangeText);
        // The slider next to the box moves the stored value.
        Assert.Equal((0.0, 1.0, 0.01), (dead.Minimum, dead.Maximum, dead.Step));
        Assert.Contains("0 .. 100", vm.AxisTuningUnitsText);
        Assert.True(vm.AxisTuningShowsPercent);

        dead.Text = "8";
        Assert.Equal(0.08, dead.Value);
        Assert.False(dead.HasInputNote);
        Assert.Equal("100", Editor(vm, "saturationY").Text);
        // More decimals than DCS's dialog takes are fine; 12.34 is 0.1234 and no rounding noise.
        Editor(vm, "saturationY").Text = "91.25";
        Assert.Equal(0.9125, Editor(vm, "saturationY").Value);
        Editor(vm, "curvature").Text = "-35";
        Assert.Equal(-0.35, Editor(vm, "curvature").Value);
        // The slider writes the value: the box follows in its unit, without float noise.
        dead.Value = 0.07 * 3;
        Assert.Equal("21", dead.Text);
        dead.Text = "8";

        // The curve and the engine get the stored unit, for this scope and for another axis.
        Assert.Equal(0.08, vm.BuildAxisTuningFromEditors(out _)!.Deadzone);
        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set"));
        Assert.Equal("0.08", After(set, "--deadzone"));
        Assert.Equal("0.9125", After(set, "--saturation-y"));
        Assert.Equal("-0.35", After(set, "--curvature"));
    }

    // 2.6.3 and before took 0.08: it is valid now too, so it is not refused, but the page asks.
    [Fact]
    public async Task OldHabit_ZeroPointZeroEight_IsKeptAndAsked()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake);
        await AxisTuningTests.SelectScopeAsync(vm, "game:DCS");
        AxisParameterEditor dead = Editor(vm, "deadzone");
        dead.Text = "0.08";
        Assert.Equal(0.0008, dead.Value);
        Assert.Equal(string.Empty, vm.AxisTuningValidation);
        Assert.Equal("Dead zone: 0.08 here means 0.08 of 100. Did you mean 8?", dead.InputNote);
        dead.Text = "8";
        Assert.False(dead.HasInputNote);

        // The same on a user curve point: under the 11 boxes.
        AxisParameterEditor curve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
        curve.IsOn = true;
        curve.Points[5].Text = "0.5";
        Assert.Equal("User curve at 50%: 0.5 here means 0.5 of 100. Did you mean 50?", curve.InputNote);
        curve.Points[5].Text = "50";
        Assert.False(curve.HasInputNote);
        Assert.Contains("0 .. 100", curve.CurveHint);
        Assert.Contains("0 .. 100", curve.Points[5].Tooltip);
        Assert.Contains("Shift: 10", curve.Points[5].Tooltip);
    }

    // Only DCS's numbers are scaled: its dialog says so. Every other model says its own unit, and no box of another game
    // becomes a percentage (Falcon BMS's steps 0..3, FSX's 0..127, Arma 3's dead zone 0..0.1).
    [Fact]
    public void EveryModel_StatesItsUnit_OnlyDcsIsScaled()
    {
        foreach (AxisModel model in AxisModels.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Units), model.Id + " does not say its unit");
            foreach (AxisParameter p in model.Parameters)
                Assert.Equal(model.Id == AxisModels.Dcs && p.Kind != AxisParameterKind.Toggle ? 100.0 : 1.0, p.DisplayScale);
        }
        foreach (AxisParameter p in AxisModels.Get(AxisModels.Dcs).Parameters.Where(p => p.Kind != AxisParameterKind.Toggle))
            Assert.True(p.Max == 1 && (p.Min == 0 || p.Min == -1), p.Key);

        Assert.Equal("0 .. 3", new AxisParameterEditor(AxisModels.ForGame("FalconBMS").Parameters.First(p => p.Kind == AxisParameterKind.Number)).RangeText);
        Assert.Equal("0 .. 127", new AxisParameterEditor(AxisModels.ForGame("FSX").Parameters.First(p => p.Kind == AxisParameterKind.Number)).RangeText);
        AxisParameterEditor xplaneDead = new(AxisModels.ForGame("XPlane12").Parameters.First(p => p.Key == "deadzone"));
        Assert.Equal("0 .. 1", xplaneDead.RangeText);
        xplaneDead.Text = "0.08";
        Assert.Equal(0.08, xplaneDead.Value);
        Assert.False(xplaneDead.HasInputNote);
        // The curve point of a game without the scale.
        AxisCurvePointEditor point = new(0.5);
        Assert.Equal((1.0, 0.01), (point.BoxMaximum, point.BoxStep));
        Assert.Contains("0 .. 1", point.Tooltip);

        string units = MainWindowViewModel.AxisUnitsText(AxisModels.ForGame("FalconBMS"));
        Assert.StartsWith("Units: ", units);
        Assert.Contains("steps", units);
    }

    // The curve's numbers and the live line: as DCS's dialog shows In / Out (a slider axis 0 .. 100); other games as before.
    [Fact]
    public async Task LiveOutput_FollowsTheUnit()
    {
        Assert.Equal("35", AxisCurveView.Number(0.35, true, false));
        Assert.Equal("-35", AxisCurveView.Number(-0.35, true, false));
        Assert.Equal("50", AxisCurveView.Number(0, true, true));
        Assert.Equal("100", AxisCurveView.Number(1, true, true));
        Assert.Equal("0", AxisCurveView.Number(-1, true, true));
        Assert.Equal("0.35", AxisCurveView.Number(0.35, false, false));

        AxisTuningFakeEngine fake = new();
        AxisTuningFakeCapture capture = new();
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake, capture);
        vm.CurrentStage = UiStage.Binds;
        await AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50");
        Editor(vm, "invert").IsOn = false;
        Editor(vm, "slider").IsOn = false;
        Editor(vm, "curvature").Text = "0";
        Editor(vm, "deadzone").Text = "0";
        vm.AxisTuningStickInput = 0.5;
        Editor(vm, "saturationY").Text = "80";
        Assert.False(vm.AxisTuningOneSided);
        Assert.Equal("in 50 -> out 40", vm.AxisTuningLiveText);
        Editor(vm, "slider").IsOn = true;
        Assert.True(vm.AxisTuningOneSided);
        // Half of the travel on a slider axis: stick 0.5 is 75 % of it.
        Assert.StartsWith("in 75 -> out ", vm.AxisTuningLiveText);

        // Another game keeps -1..1.
        await AxisTuningTests.SelectScopeAsync(vm, "game:IL2Korea");
        Assert.False(vm.AxisTuningShowsPercent);
        vm.AxisTuningStickInput = 0.5;
        vm.AxisTuningEditors.Single(e => e.Key == "sensitivity").Text = "0";
        Assert.StartsWith("in 0.50 -> out ", vm.AxisTuningLiveText);
    }

    // A loaded value with more decimals than the box shows: nothing rewrites it until the user edits it.
    [Fact]
    public async Task LoadedDecimals_SurviveALoadAndASave()
    {
        AxisTuningFakeEngine fake = new();
        fake.Scopes["plane:DCS:F-16C_50"] = "inv=0;sl=0;dz=0.085;sx=0.123456;sy=1;cv=0|0.123456|0.2|0.3|0.4|0.5|0.6|0.7|0.8|0.9|1";
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake);
        await AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50");
        Assert.Equal("8.5", Editor(vm, "deadzone").Text);
        Assert.Equal("12.3456", Editor(vm, "saturationX").Text);
        AxisParameterEditor curve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
        Assert.Equal("12.3456", curve.Points[1].Text);
        Assert.False(vm.AxisTuningHasEdits);

        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set"));
        Assert.Equal("0.085", After(set, "--deadzone"));
        Assert.Equal("0.123456", After(set, "--saturation-x"));
        Assert.Equal("0.123456", After(set, "--curvature").Split(',')[1]);
    }

    // The boxes on the page: Enter puts a number outside the range back and says so next to the box; a loaded 8.5 is not
    // touched by the slider beside it (it snaps to whole numbers only when moved).
    [Fact]
    public void OnThePage_OutOfRange_IsSaid_ALoadedValueIsLeftAlone()
    {
        AvaloniaRenderThread.Run(() =>
        {
            AxisTuningFakeEngine fake = new();
            fake.Scopes["plane:DCS:F-16C_50"] = "inv=0;sl=0;dz=0.085;sx=1;sy=1;cv=0";
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake));
            vm.CurrentStage = UiStage.Binds;
            AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50"));
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 2400));
            main.Settle();
            AxisParameterEditor dead = Editor(vm, "deadzone");
            Assert.Equal(0.085, dead.Value);
            Assert.Equal("8.5", dead.Text);
            Assert.False(vm.AxisTuningHasEdits);

            Control panel = main.Page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "AxisResponsePanel");
            TextBox box = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "AxisParameterNumber" && t.DataContext == dead);
            box.Text = "150";
            Assert.Contains("must be 0 .. 100", vm.AxisTuningValidation);
            Key(box, Avalonia.Input.Key.Enter);
            main.Settle();
            Assert.Equal("100", box.Text);
            Assert.Equal(1.0, dead.Value);
            Assert.Equal(string.Empty, vm.AxisTuningValidation);
            Assert.Equal("Dead zone: 150 is outside 0 .. 100, so it is now 100.", dead.InputNote);
            TextBlock note = panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "AxisParameterNote" && t.DataContext == dead);
            Assert.True(note.IsEffectivelyVisible);
            // The next edit clears it. Up / Down step by 1 here.
            box.Text = "8";
            Assert.False(dead.HasInputNote);
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Up, Source = box });
            Assert.Equal("9", box.Text);
            Assert.Equal(0.09, dead.Value);
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Up, KeyModifiers = KeyModifiers.Shift, Source = box });
            Assert.Equal("19", box.Text);
        });
    }
}

// The same with the real engine: DCS file values, Copy to another axis, the saved profile.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AxisUnitsRealEngineTests
{
    private const string Throttle = "VPC Throttle {00000000-0000-0000-0000-0000000000d1}";

    internal static async Task<string> RunAsync(DataDirEngineClient client, string engine, params string[] args)
    {
        EngineCommandResult result = await client.RunAsync(engine, args);
        Assert.True(result.ExitCode == 0, string.Join(" ", args) + ": " + result.StandardError);
        return result.StandardOutput;
    }

    private static string Scope(string show, string scope)
    {
        using JsonDocument doc = JsonDocument.Parse(show[show.IndexOf('{')..]);
        return doc.RootElement.GetProperty("Scopes").GetProperty(scope).GetString() ?? string.Empty;
    }

    [Fact]
    public async Task EightSavedIsZeroPointZeroEight_ALongValueIsNotRewritten_CopyKeepsTheUnit()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        Assert.NotNull(engine);
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "units-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "units.pr0file");
        MainWindowViewModel? vm = null;
        try
        {
            foreach ((string name, string axis) in new[] { ("Thrust Left", "JOY_Z"), ("Thrust Right", "JOY_RZ") })
                await RunAsync(client, engine!, "--profile", profile, "--status", "false", "--remap-missing-devices", "false", "relations", "add", name, "--axis",
                    "--node", "DCS:a2004cdnil:F-16C_50", "--device", Throttle, "--input", axis);
            // A value as DCS's own float slider leaves it in a file: more decimals than a tuning string keeps.
            await RunAsync(client, engine!, "--profile", profile, "--status", "false", "--remap-missing-devices", "false", "binds", "tuning", "set", "Thrust Left", "--scope", "base",
                "--deadzone", "0.0799999982118607", "--saturation-y", "0.123456789");
            string before = Scope(await RunAsync(client, engine!, "--profile", profile, "binds", "tuning", "show", "Thrust Left"), "base");
            Assert.Contains("dz=0.0799999982118607", before);
            Assert.Contains("sy=0.123456789", before);

            vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine!, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Binds;
            vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Thrust Left");
            vm.IsAxisTuningExpanded = true;
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && !vm.IsBusy);
            vm.SelectedAxisTuningScope = vm.AxisTuningScopes.Single(s => s.IsBase);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && vm.SelectedAxisTuningScope!.IsBase && !vm.IsBusy);
            AxisParameterEditor dead = vm.AxisTuningEditors.Single(e => e.Key == "deadzone");
            Assert.Equal("8", dead.Text);
            Assert.Equal("12.3457", vm.AxisTuningEditors.Single(e => e.Key == "saturationY").Text);
            Assert.False(vm.AxisTuningHasEdits);

            // Saved without an edit: every decimal as it was.
            await vm.SaveAxisTuningCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && !vm.IsBusy);
            Assert.Equal(before, Scope(await RunAsync(client, engine!, "--profile", profile, "binds", "tuning", "show", "Thrust Left"), "base"));

            // Typed 12 and saved: the file has 0.12, the other value still all of its decimals.
            dead = vm.AxisTuningEditors.Single(e => e.Key == "deadzone");
            dead.Text = "12";
            await vm.SaveAxisTuningCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && !vm.IsBusy);
            string after = Scope(await RunAsync(client, engine!, "--profile", profile, "binds", "tuning", "show", "Thrust Left"), "base");
            Assert.Contains("dz=0.12;", after);
            Assert.Contains("sy=0.123456789", after);

            // Copy to another axis: the other axis gets the file's unit too.
            vm.PrepareAxisTuningCopyCommand.Execute(null);
            vm.AxisTuningCopyTarget = vm.AxisTuningCopyTargets.Single(t => t.Relation == "Thrust Right");
            await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => !vm.IsBusy);
            string other = Scope(await RunAsync(client, engine!, "--profile", profile, "binds", "tuning", "show", "Thrust Right"), "base");
            Assert.Contains("dz=0.12;", other);
            Assert.Contains("sy=0.123456789", other);

            // What the command line says about units.
            string models = await RunAsync(client, engine!, "binds", "tuning", "models", "--text");
            Assert.Contains("Units: as in DCS's axis tune dialog", models);
        }
        finally
        {
            vm?.StopGameConfigWatcher();
            EngineServerPool.Recycle("units test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// "No action in Falcon BMS (4)": the relations of the list that lack an action in the game or aircraft ticked.
[SupportedOSPlatform("windows")]
public sealed class NoActionFilterTests
{
    private const string Icp = "WINWING ICP {00000000-0000-0000-0000-0000000000c1}";
    private const string Mfd = "Cougar MFD {00000000-0000-0000-0000-0000000000c2}";

    private static FilterSubject Subject(string name, string gamePlanes, string device = "", bool axis = false) =>
        new(name, [], gamePlanes.Split(',', StringSplitOptions.RemoveEmptyEntries), device, device.Length > 0, axis, name);

    private static List<FilterSubject> Sample() =>
    [
        Subject("ICP 1", "FalconBMS|FalconBMS,DCS|F-16C_50", Icp),
        Subject("ICP 2", "DCS|F-16C_50", Icp),
        Subject("ICP 3", "DCS|F-16C_50,DCS|A-10C", Icp),
        Subject("ICP 4", "FalconBMS|FalconBMS", Icp),
        Subject("MFD 1", "DCS|F-16C_50", Mfd),
        Subject("Not bound yet", "DCS|A-10C"),
        Subject("Empty", ""),
    ];

    private static List<string> Shown(ListFilterViewModel filter, IEnumerable<FilterSubject> subjects) =>
        subjects.Where(filter.Matches).Select(s => s.Name).ToList();

    [Fact]
    public void GameAndDevice_ListWhatIsMissing_WithACount()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        Assert.False(filter.HasGameFilter);

        filter.SelectGame("FalconBMS");
        Assert.True(filter.HasGameFilter);
        Assert.False(filter.NoActionInGames);
        Assert.Equal(["ICP 1", "ICP 4"], Shown(filter, subjects));
        Assert.Equal("No action in Falcon BMS (5)", filter.NoActionLabel);
        Assert.Contains("Falcon BMS", filter.NoActionTooltip);

        // With the device: the tester's list.
        filter.SelectDevices(Icp);
        Assert.Equal("No action in Falcon BMS (2)", filter.NoActionLabel);
        filter.ToggleNoActionInGamesCommand.Execute(null);
        Assert.True(filter.NoActionInGames);
        Assert.Equal(["ICP 2", "ICP 3"], Shown(filter, subjects));
        Assert.Equal(2, filter.ShownCount);
        Assert.Equal("2 of 7 shown", filter.ResultText);
        // The game's count in the Games list and the chip below say what they said: what the tick lists next to it.
        Assert.Equal(2, filter.Games.Single(g => g.Game == "FalconBMS").Count);
        // The active filters name it; its x gives the plain game filter back.
        FilterChipItem chip = filter.ActiveChips.Single(c => c.Label == "No action in Falcon BMS");
        Assert.DoesNotContain(filter.ActiveChips, c => c.Label == "Falcon BMS");
        // Other filters count with it.
        Assert.Equal(2, filter.BoundCount);
        Assert.Equal(0, filter.UnboundCount);
        chip.Remove.Execute(null);
        Assert.False(filter.NoActionInGames);
        Assert.Equal(["ICP 1", "ICP 4"], Shown(filter, subjects));
        Assert.Contains(filter.ActiveChips, c => c.Label == "Falcon BMS");

        // The typed search and the other filters narrow it.
        filter.ToggleNoActionInGamesCommand.Execute(null);
        filter.SearchText = "icp 3";
        Assert.Equal(["ICP 3"], Shown(filter, subjects));
        Assert.Equal("No action in Falcon BMS (1)", filter.NoActionLabel);
        filter.SearchText = string.Empty;
        filter.SetKindCommand.Execute("axis");
        Assert.Empty(Shown(filter, subjects));
        Assert.Equal("No action in Falcon BMS (0)", filter.NoActionLabel);
    }

    [Fact]
    public void Aircraft_AndSeveralTicks_AreTheOppositeOfThePlainFilter()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        filter.SelectOnlyPlanes("DCS", "A-10C");
        Assert.Equal("No action for A-10C (5)", filter.NoActionLabel);
        filter.NoActionInGames = true;
        Assert.Equal(["ICP 1", "ICP 2", "ICP 4", "MFD 1", "Empty"], Shown(filter, subjects));
        Assert.Contains(filter.ActiveChips, c => c.Label == "No action for A-10C");

        // Two ticks: the plain filter lists an action in any of them, this the rest: exactly the other relations.
        filter.SelectGame("FalconBMS");
        Assert.True(filter.NoActionInGames);
        Assert.Equal(["ICP 2", "MFD 1", "Empty"], Shown(filter, subjects));
        Assert.Equal("No action in A-10C, Falcon BMS (3)", filter.NoActionLabel);
        filter.NoActionInGames = false;
        Assert.Equal(["ICP 1", "ICP 3", "ICP 4", "Not bound yet"], Shown(filter, subjects));
    }

    // It needs a tick: the last game unticked or Clear all: off, and the next tick is the plain filter again.
    [Fact]
    public void WithoutAGame_ItGoesOff()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        filter.NoActionInGames = true;
        Assert.False(filter.NoActionInGames);

        filter.SelectGame("FalconBMS");
        filter.NoActionInGames = true;
        filter.ClearGamesCommand.Execute(null);
        Assert.False(filter.NoActionInGames);
        Assert.False(filter.HasGameFilter);
        filter.SelectGame("FalconBMS");
        Assert.False(filter.NoActionInGames);

        filter.NoActionInGames = true;
        filter.ClearAll();
        Assert.False(filter.NoActionInGames);

        // The game itself goes from the list (the last relation with it removed).
        filter.SelectGame("FalconBMS");
        filter.NoActionInGames = true;
        filter.SetSubjects(subjects.Where(s => !s.Games.Contains("FalconBMS")));
        Assert.False(filter.HasGameFilter);
        Assert.False(filter.NoActionInGames);
    }

    // Project rule: every filter survives a restart (list-preferences.json); a file from before has none of it.
    [Fact]
    public void State_SurvivesARestart()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        filter.SelectGame("FalconBMS");
        filter.SelectDevices(Icp);
        filter.NoActionInGames = true;

        string file = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "no-action-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            ListPreferences saved = new();
            saved.Filters[filter.Page] = filter.ToState();
            saved.Save(file);
            Assert.Contains("NoActionInGames", File.ReadAllText(file));

            ListFilterViewModel again = new("relations");
            again.LoadState(ListPreferences.Load(file).Filters["relations"]);
            again.SetSubjects(subjects);
            Assert.True(again.NoActionInGames);
            Assert.Equal(["ICP 2", "ICP 3"], Shown(again, subjects));
            Assert.Equal("No action in Falcon BMS (2)", again.NoActionLabel);
        }
        finally
        {
            File.Delete(file);
        }

        // A state without the field, or with it but no game left: plain.
        ListFilterViewModel old = new("relations");
        old.LoadState(new ListFilterState { Games = ["FalconBMS"] });
        Assert.False(old.NoActionInGames);
        ListFilterViewModel noGame = new("relations");
        noGame.LoadState(new ListFilterState { NoActionInGames = true });
        Assert.False(noGame.NoActionInGames);
    }

    [Fact]
    public void BindsPage_SaysItsOwnWords_TheNotBoundChip_SaysWhatItMeans()
    {
        ListFilterViewModel relations = new("relations");
        ListFilterViewModel binds = new("binds");
        List<FilterSubject> subjects = Sample();
        relations.SetSubjects(subjects);
        binds.SetSubjects(subjects);
        relations.SelectGame("FalconBMS");
        binds.SelectGame("FalconBMS");
        Assert.DoesNotContain("their input", relations.NoActionTooltip);
        Assert.Contains("their input does nothing there", binds.NoActionTooltip);
        binds.NoActionInGames = true;
        Assert.Equal(["ICP 2", "ICP 3", "MFD 1", "Not bound yet", "Empty"], Shown(binds, subjects));

        relations.SetBoundCommand.Execute("unbound");
        Assert.Equal("Only relations with no input at all, in any game", relations.ActiveChips.Single(c => c.Label == "Not bound").Tooltip);
    }
}

// The filter on a real profile: a device with relations, some with a BMS action, some DCS only; the action added from the
// real search; the Binds page.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class NoActionFilterRealEngineTests
{
    private const string Icp = "WINWING ICP {00000000-0000-0000-0000-0000000000c1}";
    private const string Mfd = "Cougar MFD {00000000-0000-0000-0000-0000000000c2}";

    private static async Task<(MainWindowViewModel Vm, string Folder)> OpenAsync(string engine)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "noaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "icp.pr0file");
        (string Name, string Device, string Input, string[] Nodes)[] relations =
        [
            ("ICP 1", Icp, "JOY_BTN1", ["DCS:d3001pnilu3001cd17vd1vpnilvu0:F-16C_50", "FalconBMS:SimICPTILS:FalconBMS"]),
            ("ICP 2", Icp, "JOY_BTN2", ["DCS:d3002pnilu3002cd17vd1vpnilvu0:F-16C_50"]),
            ("ICP 3", Icp, "JOY_BTN3", ["DCS:d3003pnilu3003cd17vd1vpnilvu0:F-16C_50"]),
            ("ICP 4", Icp, "JOY_BTN4", ["FalconBMS:SimICPALOW:FalconBMS"]),
            ("MFD 1", Mfd, "JOY_BTN1", ["DCS:d3004pnilu3004cd17vd1vpnilvu0:F-16C_50"])
        ];
        foreach ((string name, string device, string input, string[] nodes) in relations)
        {
            List<string> add = ["--profile", profile, "--status", "false", "--remap-missing-devices", "false", "relations", "add", name, "--button", "--device", device, "--input", input];
            foreach (string node in nodes)
                add.AddRange(["--node", node]);
            await AxisUnitsRealEngineTests.RunAsync(client, engine, add.ToArray());
        }
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        return (vm, folder);
    }

    // On the UI thread: the list drops the finished relation through the dispatcher, after the selection has settled.
    [Fact]
    public void TheTestersList_ShrinksAsHeAddsActions_AndTheBindsPageKnowsIt() =>
        AvaloniaRenderThread.Run(() => AvaloniaRenderThread.Wait(TheTestersListAsync()));

    private static async Task TheTestersListAsync()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        Assert.NotNull(engine);
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine!);
        try
        {
            vm.RelationFilter.SelectGame("FalconBMS");
            Assert.Equal(["ICP 1", "ICP 4"], vm.Relations.Select(r => r.Name).ToArray());
            vm.RelationFilter.SelectDevices(Icp);
            Assert.Equal("No action in Falcon BMS (2)", vm.RelationFilter.NoActionLabel);
            vm.RelationFilter.ToggleNoActionInGamesCommand.Execute(null);
            Assert.Equal(["ICP 2", "ICP 3"], vm.Relations.Select(r => r.Name).ToArray());
            Assert.Contains("No action in Falcon BMS", vm.RelationFiltersSummary);

            // The page's Sort works on it.
            vm.RelationSort.Add("name", true);
            Assert.Equal(["ICP 3", "ICP 2"], vm.Relations.Select(r => r.Name).ToArray());
            vm.RelationSort.Reset();

            // He opens ICP 2 and adds a BMS action from the real search.
            vm.SelectedRelation = vm.Relations.Single(r => r.Name == "ICP 2");
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.BuilderRelationName == "ICP 2" && !vm.IsBusy);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.RelationNodes.Count == 1 && !vm.IsBuilderSuggestLoading);
            vm.ShowBuilderFindCommand.Execute(null);
            vm.BuilderSearchText = "ICP";
            await SameMeaningRealEngineTests.WaitLongAsync(() => !vm.IsBuilderSearching && vm.BuilderLastSearchedQueryForTests == "ICP" && vm.BuilderSearchGames.Any(c => c.GameId == "FalconBMS"));
            vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
            await SameMeaningRealEngineTests.WaitLongAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Count > 0 && vm.BuilderSearchResults.All(r => r.Game == "FalconBMS"));
            BuilderSearchResultItem row = vm.BuilderSearchResults.First(r => r.CanCheck);
            row.IsChecked = true;
            await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.RelationNodes.Count == 2 && !vm.IsBusy && !vm.IsBuilderSuggestLoading);

            // The count says one is left; ICP 2 stays in the list while it is open (the editor does not close under him).
            Assert.Equal("No action in Falcon BMS (1)", vm.RelationFilter.NoActionLabel);
            Assert.Equal("ICP 2", vm.SelectedRelation?.Name);
            Assert.Contains(vm.Relations, r => r.Name == "ICP 2");

            // Opening the next one takes it off.
            vm.SelectedRelation = vm.Relations.Single(r => r.Name == "ICP 3");
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.Relations.Count == 1);
            Assert.Equal(["ICP 3"], vm.Relations.Select(r => r.Name).ToArray());
            Assert.Equal("ICP 3", vm.SelectedRelation?.Name);

            // The Binds page shares the class: its own ticks, its own words.
            vm.CurrentStage = UiStage.Binds;
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => !vm.IsBusy);
            vm.BindFilter.SelectGame("FalconBMS");
            vm.BindFilter.SelectDevices(Mfd);
            Assert.Equal("No action in Falcon BMS (1)", vm.BindFilter.NoActionLabel);
            vm.BindFilter.ToggleNoActionInGamesCommand.Execute(null);
            Assert.Equal(["MFD 1"], vm.Binds.Select(b => b.RelationName).ToArray());
            Assert.Contains("their input does nothing there", vm.BindFilter.NoActionTooltip);
        }
        finally
        {
            vm.StopGameConfigWatcher();
            EngineServerPool.Recycle("no action filter test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// The detent section says plainly what is calibrated, per device axis; Undo really brings back the state before it.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class DetentExplanationRealEngineTests
{
    private const string Throttle = "VPC Throttle {00000000-0000-0000-0000-0000000000d1}";

    [Fact]
    public async Task TheCalibrationBelongsToTheAxis_UndoTakesItBack()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        Assert.NotNull(engine);
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "detent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "detent.pr0file");
        MainWindowViewModel? vm = null;
        try
        {
            foreach ((string name, string axis) in new[] { ("Throttle Left", "JOY_Z"), ("Throttle Left Mig", "JOY_Z"), ("Throttle Right", "JOY_RZ") })
                await AxisUnitsRealEngineTests.RunAsync(client, engine!, "--profile", profile, "--status", "false", "--remap-missing-devices", "false", "relations", "add", name, "--axis",
                    "--node", "DCS:a2004cdnil:F-16C_50", "--device", Throttle, "--input", axis);

            vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine!, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Binds;
            async Task OpenAsync(string relation)
            {
                vm!.SelectedBind = vm.Binds.Single(b => b.RelationName == relation);
                vm.IsDetentExpanded = true;
                await SameMeaningRealEngineTests.WaitLongAsync(() => vm.DetentLoaded && !vm.IsBusy);
            }

            // Without a calibration: said once, in the state line, with what it means.
            await OpenAsync("Throttle Left");
            Assert.False(vm.DetentHasCalibration);
            Assert.Equal("VPC Throttle JOY_Z is not calibrated: detent mode changes nothing on this axis yet, every aircraft keeps the relation's curve. Run Calibrate detent...", vm.DetentCalibrationText);
            Assert.DoesNotContain("not calibrated", vm.DetentWarningText);

            await AxisUnitsRealEngineTests.RunAsync(client, engine!, "--profile", profile, "--remap-missing-devices", "false", "binds", "detent", "calibrate", "Throttle Left",
                "--idle", "0.03", "--detent", "0.79", "--full", "0.99", "--source", "wizard");
            await vm.ReloadDetentCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.DetentLoaded && !vm.IsBusy);
            Assert.True(vm.DetentHasCalibration);
            Assert.StartsWith("VPC Throttle JOY_Z is calibrated: idle at 3 %, afterburner detent at 79 %, full at 99 % of the travel (measured ", vm.DetentCalibrationText);

            // Another relation on the same axis shares it; the other lever is another axis.
            await OpenAsync("Throttle Left Mig");
            Assert.True(vm.DetentHasCalibration);
            Assert.Contains("VPC Throttle JOY_Z is calibrated", vm.DetentCalibrationText);
            await OpenAsync("Throttle Right");
            Assert.False(vm.DetentHasCalibration);
            Assert.Contains("VPC Throttle JOY_RZ is not calibrated", vm.DetentCalibrationText);

            // Undo: the state before the calibration, as the tooltip and the docs say. (The profile on disk is what counts.)
            await AxisUnitsRealEngineTests.RunAsync(client, engine!, "--profile", profile, "profile", "history", "undo");
            await OpenAsync("Throttle Left");
            await vm.ReloadDetentCommand.ExecuteAsync(null);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.DetentLoaded && !vm.IsBusy);
            Assert.False(vm.DetentHasCalibration);
            Assert.Contains("is not calibrated", vm.DetentCalibrationText);
        }
        finally
        {
            vm?.StopGameConfigWatcher();
            EngineServerPool.Recycle("detent explanation test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// The search's wording when nothing new was found, every path, with the rows the engine gives.
[SupportedOSPlatform("windows")]
public sealed class NothingNewWordingTests
{
    internal static string Hit(string game, string id, string title, string section, string plane) =>
        "{\"Game\":\"" + game + "\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"Weapons\",\"IsAxis\":false,\"Planes\":[\"" + plane + "\"]," +
        (game == "DCS" ? "\"Variants\":[{\"Title\":\"" + title + "\",\"Category\":\"Weapons\",\"Match\":\"same\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"" + plane + "\"]}]," : string.Empty) +
        "\"Score\":900,\"Literal\":" + (section == "name" ? "true" : "false") + ",\"Why\":\"\",\"Match\":\"" + (section == "name" ? "function" : "synonym") + "\",\"Function\":\"pickle\"," +
        "\"Display\":\"" + title + "\",\"InRelations\":[],\"Recommended\":false,\"Variant\":\"\",\"Section\":\"" + section + "\"}";

    internal static string Result(params string[] hits) =>
        "{\"Query\":\"q\",\"Total\":" + hits.Length + ",\"Functions\":[],\"Resolved\":[],\"Results\":[" + string.Join(",", hits) + "],\"Groups\":[{\"Game\":\"DCS\",\"Count\":" + hits.Length + ",\"Total\":" + hits.Length + "}],\"FunctionGroups\":[],\"Warnings\":[],\"Errors\":[]}";

    internal static async Task<MainWindowViewModel> SearchedAsync(string file, (string Game, string Id, string Plane)[] nodes, params string[] hits)
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, nodes);
        fake.SearchOverride = _ => Result(hits);
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, file);
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        vm.BuilderSearchText = "pickle it";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "pickle it" && !vm.IsBuilderSearching);
        return vm;
    }

    // Every result is in the relation, nothing hidden: no callout, Same Meaning on or off.
    [Fact]
    public async Task EveryRowInTheRelation_NothingHidden_OnlyTheStatusLine()
    {
        MainWindowViewModel vm = await SearchedAsync("nothing-new-1.pr0file", [("DCS", "d1", "AJS37"), ("DCS", "d2", "F-14")],
            Hit("DCS", "d1", "Pickle It", "name", "AJS37"), Hit("DCS", "d2", "Pickle It", "name", "F-14"));
        foreach (bool sameMeaning in new[] { true, false })
        {
            vm.BuilderShowSameMeaning = sameMeaning;
            Assert.False(vm.ShowBuilderNoNameMatches);
            Assert.False(vm.ShowBuilderChosenGamesEmpty);
            Assert.Equal("No new actions: the 2 found are already in this relation (see below)", vm.BuilderSearchStatus);
            Assert.Equal(2, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count(r => r.IsInOpenRelation));
        }
    }

    // The name matches are in the relation, others hidden by Same meaning off: the short callout.
    [Fact]
    public async Task NameMatchesInTheRelation_OthersHidden_ShortCallout()
    {
        MainWindowViewModel vm = await SearchedAsync("nothing-new-2.pr0file", [("DCS", "d1", "AJS37"), ("DCS", "d2", "F-14")],
            Hit("DCS", "d1", "Pickle It", "name", "AJS37"), Hit("DCS", "d2", "Pickle It", "name", "F-14"), Hit("DCS", "d3", "Drop", "function", "M-2000C"), Hit("DCS", "d4", "Release", "function", "M-2000C"));
        vm.BuilderShowSameMeaning = false;
        Assert.True(vm.ShowBuilderNoNameMatches);
        Assert.True(vm.CanShowBuilderSameMeaning);
        Assert.Equal("No new actions with every word of \"pickle it\": the 2 found are already in this relation (see below). 2 more found by what they do are hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
        Assert.Equal("No new actions + 2 already in this relation · 2 with the same meaning hidden (Same meaning is off)", vm.BuilderSearchStatus);
        // On: they are listed; nothing to say.
        vm.ShowBuilderSameMeaningCommand.Execute(null);
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.StartsWith("2 actions in 1 game + 2 already in this relation", vm.BuilderSearchStatus);
    }

    // Numbers of one.
    [Fact]
    public async Task OneOfEach_ReadsAsOne()
    {
        MainWindowViewModel vm = await SearchedAsync("nothing-new-3.pr0file", [("DCS", "d1", "AJS37")],
            Hit("DCS", "d1", "Pickle It", "name", "AJS37"), Hit("DCS", "d3", "Drop", "function", "M-2000C"));
        vm.BuilderShowSameMeaning = false;
        Assert.Equal("No new actions with every word of \"pickle it\": the 1 found is already in this relation (see below). 1 more found by what it does is hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
    }

    // No name match anywhere the search looked: it does not claim no action's name has them (the filters narrowed the search).
    [Fact]
    public async Task NoNameMatch_OnlyHiddenRows_SaysWhatWasFound()
    {
        MainWindowViewModel vm = await SearchedAsync("nothing-new-4.pr0file", [("DCS", "d9", "AJS37")],
            Hit("DCS", "d1", "Drop", "function", "F-14"), Hit("DCS", "d2", "Release", "function", "M-2000C"));
        vm.BuilderShowSameMeaning = false;
        Assert.True(vm.ShowBuilderNoNameMatches);
        Assert.Equal("No action found whose name has every word of \"pickle it\". 2 found by what they do are hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
        Assert.Equal("No new actions · 2 with the same meaning hidden (Same meaning is off)", vm.BuilderSearchStatus);
    }

    // The only rows are the relation's and the switch hides them too: the callout brings them back; no "see below".
    [Fact]
    public async Task OnlyRowsOfTheRelation_AllHidden_NoSeeBelow()
    {
        MainWindowViewModel vm = await SearchedAsync("nothing-new-5.pr0file", [("DCS", "d1", "AJS37"), ("DCS", "d2", "F-14")],
            Hit("DCS", "d1", "Drop", "function", "AJS37"), Hit("DCS", "d2", "Release", "function", "F-14"));
        vm.BuilderShowSameMeaning = false;
        Assert.True(vm.ShowBuilderNoNameMatches);
        Assert.True(vm.CanShowBuilderSameMeaning);
        Assert.Equal("No new actions: the 2 found by what they do are already in this relation, hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
        Assert.Equal("No new actions · 2 more in this relation hidden", vm.BuilderSearchStatus);
        Assert.Empty(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>());
        vm.BuilderShowSameMeaning = true;
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.Equal("No new actions: the 2 found are already in this relation (see below)", vm.BuilderSearchStatus);
    }

    // The game chip on: the BMS action is in the relation. Not "Nothing found ... in Falcon BMS".
    [Fact]
    public async Task GameChip_TheOnlyRowIsInTheRelation_NotNothingFound()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, ("FalconBMS", "SimICP", "FalconBMS"));
        string row = Hit("FalconBMS", "SimICP", "ICP Pickle It", "name", "FalconBMS");
        fake.SearchOverride = args => args.Contains("FalconBMS") ? Result(row) : Result(row, Hit("DCS", "d1", "Pickle It", "name", "F-14"));
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "nothing-new-6.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        vm.BuilderSearchText = "pickle it";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "pickle it" && !vm.IsBuilderSearching && vm.BuilderSearchGames.Any(c => c.GameId == "FalconBMS"));
        vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.All(r => r.Game == "FalconBMS"));
        Assert.False(vm.ShowBuilderChosenGamesEmpty);
        Assert.DoesNotContain("Nothing found", vm.BuilderSearchStatus);
        Assert.Equal("No new actions: the 1 found is already in this relation (see below)", vm.BuilderSearchStatus);
        Assert.False(vm.ShowBuilderNoNameMatches);
    }

    // Nothing at all still says so: it really found nothing, in the game chip's words too.
    [Fact]
    public async Task ReallyNothing_StillSaysNothingFound()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, ("FalconBMS", "SimICP", "FalconBMS"));
        fake.SearchOverride = _ => Result();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "nothing-new-7.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        vm.BuilderSearchText = "pickle it";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "pickle it" && !vm.IsBuilderSearching);
        Assert.StartsWith("Nothing found for \"pickle it\"", vm.BuilderSearchStatus);
        Assert.False(vm.ShowBuilderNoNameMatches);
    }

    // The Suggested tab: the ticked game suggests nothing more because the relation has its action there.
    [Fact]
    public async Task SuggestedTab_TickedGameAlreadyInTheRelation_SaysSo()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, ("WarThunder", "SimICP", "WarThunder"));
        fake.AddRelation("Other", false, string.Empty, ("DCS", "d9", "AJS37"));
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "nothing-new-8.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        vm.BuilderSuggestionGames.Single(g => g.GameId == "WarThunder").IsSelected = true;
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSuggestLoading);
        Assert.Empty(vm.BuilderSuggestions);
        Assert.Equal("No new suggestions for War Thunder: this relation already has its action there. Tick another game or show the weak matches.", vm.BuilderSuggestEmptyText);
        // A relation without that game: the text as before.
        await RelationBuilderTests.OpenAsync(vm, "Other");
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSuggestLoading);
        Assert.DoesNotContain("already has", vm.BuilderSuggestEmptyText);
    }

    // With the real engine and an aircraft filter: the F-4E's own rows are in the relation, the search says "nothing new".
    [Fact]
    public async Task RealEngine_AircraftFilter_TheOnlyNameMatchesAreInTheRelation()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        Assert.NotNull(engine);
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "nothingnew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "stick.pr0file");
        const string pilot = "d3002pnilu3002cd27vd1vpnilvu0";
        MainWindowViewModel? vm = null;
        try
        {
            await AxisUnitsRealEngineTests.RunAsync(client, engine!, "--profile", profile, "--status", "false", "relations", "add", "Stick Red", "--node", "DCS:" + pilot + ":F-4E-45MC");
            vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine!, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Stick Red");
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.BuilderRelationName == "Stick Red" && !vm.IsBusy);
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.RelationNodes.Count == 1 && !vm.IsBuilderSuggestLoading);
            vm.ShowBuilderFindCommand.Execute(null);
            await vm.AddBuilderSearchPlaneCommand.ExecuteAsync("F-4E-45MC");
            await SameMeaningRealEngineTests.SearchAsync(vm, "Bomb Release Button");
            vm.BuilderShowSameMeaning = false;
            Assert.Contains(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>(), r => r.IsInOpenRelation);
            Assert.DoesNotContain("Nothing found", vm.BuilderSearchStatus);
            Assert.StartsWith("No new actions", vm.BuilderSearchStatus);
            if (vm.ShowBuilderNoNameMatches)
                Assert.StartsWith("No new actions with every word of \"Bomb Release Button\": the 1 found is already in this relation (see below).", vm.BuilderNoNameMatchesText);
        }
        finally
        {
            vm?.StopGameConfigWatcher();
            EngineServerPool.Recycle("nothing new test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// The pictures: the DCS axis panel, the chip, the detent section, every callout and status case; dark and light.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class TesterBatch264cRenderTests
{
    private static readonly (string Theme, ThemeVariant Variant)[] Themes = [("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light)];

    // The window at its size, cut to the control found (from `above` over it, `height` high).
    private static void Cut(MainWindowViewModel vm, Func<Control, bool> anchor, string file, int above = 12, int height = 600, int windowWidth = 1600, int windowHeight = 3000, int cutWidth = 0, Action? prepare = null)
    {
        using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(windowWidth, windowHeight));
        main.Settle();
        // A new window's scope box picks its item again, which loads the editors: what is typed comes after that.
        for (int i = 0; i < 100 && (!vm.AxisTuningLoaded || vm.IsBusy); i++)
            main.Settle();
        if (prepare != null)
        {
            prepare();
            main.Settle();
        }
        Control page = main.Page;
        Control target = page.GetVisualDescendants().OfType<Control>().First(c => anchor(c) && c.IsEffectivelyVisible);
        Point origin = target.TranslatePoint(new Point(0, 0), page) ?? new Point(0, 0);
        double x = Math.Max(0, origin.X - 16);
        double y = Math.Max(0, origin.Y - above);
        PixelSize size = new((int)Math.Min(windowWidth - x, cutWidth > 0 ? cutWidth : Math.Max(target.Bounds.Width + 32, 400)), (int)Math.Min(windowHeight - y, height));
        using RenderTargetBitmap whole = new(new PixelSize(windowWidth, windowHeight));
        using (DrawingContext context = whole.CreateDrawingContext())
        {
            IBrush background = main.Window.TryFindResource("JP.PageBackgroundBrush", main.Window.ActualThemeVariant, out object? brush) && brush is IBrush own
                ? own
                : new SolidColorBrush(main.Window.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
            context.FillRectangle(background, new Rect(0, 0, windowWidth, windowHeight));
        }
        whole.Render(page);
        using RenderTargetBitmap cut = new(size);
        using (DrawingContext context = cut.CreateDrawingContext())
            context.DrawImage(whole, new Rect(x, y, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
        cut.Save(file);
    }

    private static void Shot(Action<string> render)
    {
        foreach ((string theme, ThemeVariant variant) in Themes)
        {
            Application.Current!.RequestedThemeVariant = variant;
            render(theme);
        }
    }

    [Fact]
    public void Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_264C_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                RenderAxis(outDir);
                RenderFilter(outDir);
                RenderDetent(outDir);
                RenderCallouts(outDir);
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    private static void RenderAxis(string outDir)
    {
        AxisTuningFakeEngine fake = new();
        fake.Scopes["plane:DCS:F-16C_50"] = "inv=0;sl=0;dz=0.08;sx=1;sy=0.905;cv=0.2";
        AxisTuningFakeCapture capture = new();
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake, capture));
        vm.CurrentStage = UiStage.Binds;
        AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50"));
        vm.OnAxisTuningAxes(new JoystickAxisSnapshot(AxisTuningTests.Stick, [0.5, 0.82, 0.5, 0.5, 0.5, 0.5, 0, 0]));
        bool Anchor(Control c) => c.Name == "AxisResponsePanel";
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "axis-dcs-" + theme + ".png"), height: 760, windowWidth: 1500));

        // Notes: 0.08 typed, a number outside the range.
        void Notes()
        {
            vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "0.08";
            AxisParameterEditor sat = vm.AxisTuningEditors.Single(e => e.Key == "saturationX");
            sat.Text = "100";
            sat.OnPutBackInRange(150, 100);
        }
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "axis-dcs-notes-" + theme + ".png"), height: 800, windowWidth: 1500, prepare: Notes));

        // The user curve and the slider axis (0 .. 100 on the curve).
        void UserCurve()
        {
            AxisParameterEditor curve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
            curve.IsOn = true;
            double[] soft = [0, 4, 9, 15, 23, 33, 44, 57, 70, 85, 100];
            for (int i = 0; i < soft.Length; i++)
                curve.Points[i].Text = soft[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            curve.Points[7].Text = "0.5";
            vm.AxisTuningEditors.Single(e => e.Key == "slider").IsOn = true;
        }
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "axis-dcs-usercurve-slider-" + theme + ".png"), height: 900, windowWidth: 1500, prepare: UserCurve));
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "axis-dcs-usercurve-1280-" + theme + ".png"), height: 1040, windowWidth: 1280, prepare: UserCurve));

        // Another game keeps its units: the units line says so.
        AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "game:IL2Korea"));
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "axis-il2korea-" + theme + ".png"), height: 760, windowWidth: 1500));
    }

    private static void RenderFilter(string outDir)
    {
        BuilderFakeEngine fake = new();
        const string icp = "WINWING ICP {00000000-0000-0000-0000-0000000000c1}";
        fake.AddRelation("ICP 1", false, icp + "::JOY_BTN1", ("FalconBMS", "SimICPTILS", "FalconBMS"), ("DCS", "d1", "F-16C_50"));
        fake.AddRelation("ICP 2", false, icp + "::JOY_BTN2", ("DCS", "d2", "F-16C_50"));
        fake.AddRelation("ICP 3", false, icp + "::JOY_BTN3", ("DCS", "d3", "F-16C_50"));
        fake.AddRelation("ICP 4", false, icp + "::JOY_BTN4", ("DCS", "d4", "F-16C_50"));
        fake.AddRelation("ICP 5", false, icp + "::JOY_BTN5", ("DCS", "d5", "F-16C_50"));
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(RelationBuilderTests.CreateVmAsync(fake, "render-264c-filter.pr0file"));
        vm.CurrentStage = UiStage.Relations;
        vm.IsRelationFiltersOpen = true;
        vm.RelationFilter.SelectGame("FalconBMS");
        vm.RelationFilter.SelectDevices(icp);
        Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1600, 900, Path.Combine(outDir, "filter-chip-off-" + theme + ".png")));
        vm.RelationFilter.ToggleNoActionInGamesCommand.Execute(null);
        Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1600, 900, Path.Combine(outDir, "filter-chip-on-" + theme + ".png")));
        Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1280, 800, Path.Combine(outDir, "filter-chip-on-1280-" + theme + ".png")));
        // A long list of ticks: the chip ends in ... rather than widening the pane.
        vm.RelationFilter.SelectGame("DCS");
        Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1280, 800, Path.Combine(outDir, "filter-chip-two-games-" + theme + ".png")));
        vm.RelationFilter.ClearAll();
        vm.CurrentStage = UiStage.Binds;
        AvaloniaRenderThread.Wait(vm.RefreshBindsCommand.ExecuteAsync(null));
        vm.BindFilter.SelectGame("FalconBMS");
        vm.BindFilter.ToggleNoActionInGamesCommand.Execute(null);
        Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1600, 900, Path.Combine(outDir, "filter-chip-binds-" + theme + ".png")));
    }

    private static void RenderDetent(string outDir)
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "render-264c-detent.pr0file");
        AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
        AvaloniaRenderThread.Wait(vm.RefreshBindsCommand.ExecuteAsync(null));
        vm.CurrentStage = UiStage.Binds;
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Throttle");
        vm.IsAxisTuningExpanded = false;
        vm.IsDetentExpanded = true;
        for (int i = 0; i < 300 && !vm.DetentLoaded; i++)
            AvaloniaRenderThread.Pump();
        bool Anchor(Control c) => c.Name == "DetentExplanation";
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "detent-calibrated-" + theme + ".png"), above: 40, height: 440, windowWidth: 1500));
        // Without a calibration.
        string noCalibration = "{\"Relation\":\"Throttle\",\"Message\":\"\",\"Device\":\"VPC Throttle\",\"Axis\":\"JOY_Z\",\"DeviceLabel\":\"VPC Throttle JOY_Z\",\"Settings\":{\"Stored\":false,\"On\":false,\"NoAfterburner\":\"full\",\"Unknown\":\"linear\",\"Margin\":0.01,\"Text\":\"\"},\"Calibration\":null,\"GatesFile\":\"\",\"Groups\":[],\"Warnings\":[\"The device axis is not calibrated: run the detent wizard (binds detent calibrate).\"]}";
        vm.ApplyDetentShow(noCalibration);
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "detent-not-calibrated-" + theme + ".png"), above: 40, height: 300, windowWidth: 1500));
        Shot(theme => Cut(vm, Anchor, Path.Combine(outDir, "detent-not-calibrated-1280-" + theme + ".png"), above: 40, height: 340, windowWidth: 1280));
    }

    private static void RenderCallouts(string outDir)
    {
        void Case(string name, bool sameMeaning, (string, string, string)[] nodes, params string[] hits)
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(NothingNewWordingTests.SearchedAsync("render-264c-" + name + ".pr0file", nodes, hits));
            vm.BuilderShowSameMeaning = sameMeaning;
            File.AppendAllText(Path.Combine(outDir, "texts.txt"), "--- " + name + "\nstatus: " + vm.BuilderSearchStatus + "\ncallout (" + vm.ShowBuilderNoNameMatches + "): " + vm.BuilderNoNameMatchesText + "\n");
            Shot(theme => UiDesignRenderTests.RenderWindow(vm, 1600, 800, Path.Combine(outDir, "callout-" + name + "-" + theme + ".png")));
            vm.StopGameConfigWatcher();
        }

        File.WriteAllText(Path.Combine(outDir, "texts.txt"), string.Empty);
        (string, string, string)[] both = [("DCS", "d1", "AJS37"), ("DCS", "d2", "F-14")];
        string name1 = NothingNewWordingTests.Hit("DCS", "d1", "Pickle It", "name", "AJS37");
        string name2 = NothingNewWordingTests.Hit("DCS", "d2", "Pickle It", "name", "F-14");
        string other1 = NothingNewWordingTests.Hit("DCS", "d3", "Drop", "function", "M-2000C");
        string other2 = NothingNewWordingTests.Hit("DCS", "d4", "Release", "function", "M-2000C");
        Case("all-in-relation-same-meaning-on", true, both, name1, name2);
        Case("all-in-relation-same-meaning-off", false, both, name1, name2);
        Case("name-in-relation-others-hidden", false, both, name1, name2, other1, other2);
        Case("no-name-match-only-hidden", false, [("DCS", "d9", "AJS37")], other1, other2);
        Case("only-relation-rows-hidden", false, both, NothingNewWordingTests.Hit("DCS", "d1", "Drop", "function", "AJS37"), NothingNewWordingTests.Hit("DCS", "d2", "Release", "function", "F-14"));
    }
}
