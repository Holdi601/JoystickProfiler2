using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.6.5 tester report "I don't understand what the scope does": the axis response's "Applies to" checkboxes with the real
// engine on the tester's case, a helicopter collective with five DCS aircraft. By default the aircraft that use DCS's
// curve are ticked and Save stores DCS's curve (not five copies); a real subset gets a curve per aircraft, in one engine
// run (one Ctrl+Z); ticked aircraft with their own curve are named before they lose it; the aircraft are grouped by the
// curve they share; the ticks last saved come back when the relation is opened again, also after a restart.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AxisTuningAppliesToTests
{
    internal const string Collective = "THROTTLE - THRUST COLLECTIVE - HELO";
    internal const string Pedals = "RUDDER - PEDALS - HELO";
    internal const string Throttle = "VPC Collective {00000000-0000-0000-0000-0000000000e1}";
    internal static readonly string[] Helicopters = ["AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3", "SA342 Copilot", "SA342 Pilot"];

    private static string Plane(string id) => "plane:DCS:" + id;

    internal sealed class Setup : IAsyncDisposable
    {
        public required string Engine;
        public required string Folder;
        public required string Profile;
        public required DataDirEngineClient Client;
        public required string? PreviousData;
        public List<MainWindowViewModel> Vms { get; } = [];

        public async Task<string> RunAsync(params string[] args)
        {
            EngineCommandResult result = await Client.RunAsync(Engine, ["--profile", Profile, "--status", "false", "--remap-missing-devices", "false", .. args]);
            Assert.True(result.ExitCode == 0, string.Join(" ", args) + ": " + result.StandardError + result.StandardOutput);
            return result.StandardOutput;
        }

        // The stored scopes of a relation as the profile has them.
        public async Task<Dictionary<string, string>> ScopesAsync(string relation)
        {
            string show = await RunAsync("binds", "tuning", "show", relation);
            using JsonDocument doc = JsonDocument.Parse(show[show.IndexOf('{')..]);
            return doc.RootElement.GetProperty("Scopes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        public int Versions => JoyPro.History.ProfileHistory.Read(Profile).Versions.Count;

        public async Task<MainWindowViewModel> OpenAsync(string relation = Collective, ListPreferences? prefs = null)
        {
            MainWindowViewModel vm = new(Client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = Engine, ProfilePath = Profile };
            if (prefs != null)
                vm.ListPrefs = prefs;
            Vms.Add(vm);
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Binds;
            await SelectAsync(vm, relation);
            return vm;
        }

        public static async Task SelectAsync(MainWindowViewModel vm, string relation)
        {
            vm.SelectedBind = vm.Binds.First(b => b.RelationName == relation);
            vm.IsAxisTuningExpanded = true;
            await SettleAsync(vm);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (MainWindowViewModel vm in Vms)
                vm.StopGameConfigWatcher();
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", PreviousData);
            await RelationBuilderTests.SettleAllAsync();
            EngineServerPool.Recycle("applies-to test done");
            try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task SettleAsync(MainWindowViewModel vm)
    {
        await Task.Delay(50);
        await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && vm.SelectedAxisTuningScope != null && !vm.IsBusy && vm.AxisTuningScopes.Count > 0);
        await Task.Delay(50);
        await SameMeaningRealEngineTests.WaitLongAsync(() => vm.AxisTuningLoaded && !vm.IsBusy);
    }

    // The collective (five helicopters) and the pedals (the same five) bound on one device; nothing tuned yet.
    internal static async Task<Setup?> CreateAsync()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return null;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "applies-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(data);
        Setup setup = new()
        {
            Engine = engine,
            Folder = folder,
            Profile = Path.Combine(folder, "helos.pr0file"),
            Client = new DataDirEngineClient(data),
            PreviousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")
        };
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        string planes = string.Join("|", Helicopters);
        await setup.RunAsync("relations", "add", Collective, "--axis", "--node", "DCS:a2087cdnil:" + planes, "--device", Throttle, "--input", "JOY_Z");
        await setup.RunAsync("relations", "add", Pedals, "--axis", "--node", "DCS:a2003cdnil:" + planes, "--device", Throttle, "--input", "JOY_RZ");
        return setup;
    }

    private static AxisTuningScopeItem Row(MainWindowViewModel vm, string scope) => vm.AxisTuningScopes.Single(s => s.Scope == scope);

    private static string Names(MainWindowViewModel vm, params string[] planes) => string.Join(", ", planes.Select(p => Row(vm, Plane(p)).Label));

    // Saves within two seconds of each other are one history version (ProfileHistory.CoalesceSeconds).
    private static async Task SaveAsync(Setup setup, MainWindowViewModel vm, int expectVersions = 1)
    {
        await Task.Delay(2500);
        int before = setup.Versions;
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.Equal(before + expectVersions, setup.Versions);
    }

    // Untick the two SA342: the three others are a real subset, so they get a curve each (dead zone 12 in DCS's 0..100).
    private static async Task SaveThreeOfFiveAsync(Setup setup, MainWindowViewModel vm)
    {
        Row(vm, Plane("SA342 Copilot")).IsChecked = false;
        Row(vm, Plane("SA342 Pilot")).IsChecked = false;
        await SettleAsync(vm);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "12";
        vm.AxisTuningEditors.Single(e => e.Key == "curvature").Text = "20";
        await SaveAsync(setup, vm);
    }

    [Fact]
    public async Task DefaultTicksAllAircraft_SaveStoresTheGameCurve_ASubsetGetsOwnCurves_OneUndo()
    {
        await using Setup? setup = await CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();

        // A fresh relation: DCS and all five helicopters are ticked; Save says it stores DCS's curve.
        Assert.Equal("game:DCS", vm.SelectedAxisTuningScope!.Scope);
        Assert.Equal(["game:DCS", .. Helicopters.Select(Plane)], vm.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.Equal("Save stores it for DCS, all its aircraft (5).", vm.AxisTuningSavePlanText);
        Assert.Equal("Save for DCS", vm.AxisTuningSaveText);
        Assert.False(vm.HasAxisTuningReplaceText);
        Assert.All(Helicopters, p => Assert.Equal("uses All games", Row(vm, Plane(p)).UsesText));

        // Saved like that: DCS's curve, no copy per aircraft.
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "5";
        await SaveAsync(setup, vm);
        Dictionary<string, string> scopes = await setup.ScopesAsync(Collective);
        Assert.Contains("dz=0.05;", scopes["game:DCS"]);
        Assert.DoesNotContain(scopes.Keys, k => k.StartsWith("plane:", StringComparison.Ordinal));
        Assert.All(Helicopters, p => Assert.Equal("uses DCS", Row(vm, Plane(p)).UsesText));

        // Three of five: a curve per ticked aircraft, in one run; the two others keep DCS's.
        Row(vm, Plane("SA342 Copilot")).IsChecked = false;
        await SettleAsync(vm);
        Assert.False(Row(vm, "game:DCS").IsChecked);
        Row(vm, Plane("SA342 Pilot")).IsChecked = false;
        await SettleAsync(vm);
        Assert.Equal("Save for the ticked aircraft (3 of 5)", vm.AxisTuningSaveText);
        Assert.Equal("Save stores it for these aircraft (3 of 5): " + Names(vm, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3") + ".", vm.AxisTuningSavePlanText);
        // DCS's numbers in 0 .. 100 (its own dialog's unit) for the aircraft as for the game.
        Assert.Equal("5", vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "12";
        vm.AxisTuningEditors.Single(e => e.Key == "curvature").Text = "20";
        await SaveAsync(setup, vm);
        Assert.Contains("Ctrl+Z", vm.StatusText);
        scopes = await setup.ScopesAsync(Collective);
        foreach (string p in new[] { "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3" })
        {
            Assert.Contains("dz=0.12;", scopes[Plane(p)]);
            Assert.Contains("cv=0.2", scopes[Plane(p)]);
        }
        Assert.False(scopes.ContainsKey(Plane("SA342 Copilot")));
        Assert.False(scopes.ContainsKey(Plane("SA342 Pilot")));
        Assert.Equal("12", vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text);

        // What each uses now, and the groups by curve.
        Assert.Equal("same as " + Names(vm, "AH-64D_BLK_II_PLT", "Ka-50_3"), Row(vm, Plane("AH-64D_BLK_II_CPG")).UsesText);
        Assert.Equal("uses DCS", Row(vm, Plane("SA342 Pilot")).UsesText);
        Assert.Equal(
            [Names(vm, "SA342 Copilot", "SA342 Pilot") + ": uses DCS", Names(vm, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3") + ": same curve"],
            vm.AxisTuningGroups.Select(g => g.Text).ToArray());
        Assert.True(vm.AxisTuningGroups[1].IsCurrent);
        Assert.Equal("Shown: the own curve of " + Names(vm, "AH-64D_BLK_II_CPG") + ".", vm.AxisTuningStatus);

        // One Ctrl+Z takes the three curves back together.
        await vm.UndoLastChangeCommand.ExecuteAsync(null);
        scopes = await setup.ScopesAsync(Collective);
        Assert.DoesNotContain(scopes.Keys, k => k.StartsWith("plane:", StringComparison.Ordinal));
        Assert.Contains("dz=0.05;", scopes["game:DCS"]);
        await vm.ReloadAxisTuningCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.All(Helicopters, p => Assert.Equal("uses DCS", Row(vm, Plane(p)).UsesText));
    }

    [Fact]
    public async Task TickingAnAircraftWithItsOwnCurve_SaysItIsReplaced_AndItFollowsTheGameAgain()
    {
        await using Setup? setup = await CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();
        await SaveThreeOfFiveAsync(setup, vm);

        // The game row: the two that use DCS's curve; the three with their own stay unticked and are named.
        Row(vm, "game:DCS").IsChecked = true;
        await SettleAsync(vm);
        Assert.Equal(["game:DCS", Plane("SA342 Copilot"), Plane("SA342 Pilot")], vm.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.Equal("Save stores it for DCS: 2 of its 5 aircraft follow it; " + Names(vm, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3") + " keep their own curve.",
            vm.AxisTuningSavePlanText);
        Assert.False(vm.HasAxisTuningReplaceText);

        // One of them ticked as well: said before saving, and it follows DCS's curve afterwards (its own is removed in the
        // same run).
        Row(vm, Plane("Ka-50_3")).IsChecked = true;
        await SettleAsync(vm);
        Assert.True(Row(vm, "game:DCS").IsChecked);
        Assert.Equal("This replaces the own curve of " + Names(vm, "Ka-50_3") + ".", vm.AxisTuningReplaceText);
        Assert.Equal("Save stores it for DCS: 3 of its 5 aircraft follow it; " + Names(vm, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT") + " keep their own curve.",
            vm.AxisTuningSavePlanText);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "7";
        await SaveAsync(setup, vm);
        Dictionary<string, string> scopes = await setup.ScopesAsync(Collective);
        Assert.Contains("dz=0.07;", scopes["game:DCS"]);
        Assert.False(scopes.ContainsKey(Plane("Ka-50_3")));
        Assert.Contains("dz=0.12;", scopes[Plane("AH-64D_BLK_II_CPG")]);
        Assert.Equal("uses DCS", Row(vm, Plane("Ka-50_3")).UsesText);

        // "Use the game's curve again" for ticked aircraft: one step for both.
        vm.TickAxisTuningRows([Plane("AH-64D_BLK_II_CPG"), Plane("AH-64D_BLK_II_PLT")]);
        await SettleAsync(vm);
        Assert.Equal("Use the game's curve again", vm.AxisTuningClearText);
        Assert.True(vm.CanClearAxisTuning);
        await Task.Delay(2500);
        int before = setup.Versions;
        await vm.ClearAxisTuningCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.Equal(before + 1, setup.Versions);
        scopes = await setup.ScopesAsync(Collective);
        Assert.DoesNotContain(scopes.Keys, k => k.StartsWith("plane:", StringComparison.Ordinal));
        Assert.All(Helicopters, p => Assert.Equal("uses DCS", Row(vm, Plane(p)).UsesText));
    }

    [Fact]
    public async Task TheTicksLastSaved_ComeBack_AfterAnotherRelation_AndARestart()
    {
        await using Setup? setup = await CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();
        await SaveThreeOfFiveAsync(setup, vm);
        string[] three = [Plane("AH-64D_BLK_II_CPG"), Plane("AH-64D_BLK_II_PLT"), Plane("Ka-50_3")];
        Assert.Equal(three, vm.ListPrefs.AxisCurveTicks[Collective].ToArray());

        // Another relation: it starts on its own default (nothing saved there yet).
        await Setup.SelectAsync(vm, Pedals);
        Assert.Equal("game:DCS", vm.SelectedAxisTuningScope!.Scope);
        Assert.Equal(6, vm.AxisTuningTicked.Count);

        // Back: the same three, their curve shown, the groups as before.
        await Setup.SelectAsync(vm, Collective);
        Assert.Equal(three, vm.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.Equal("12", vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text);
        Assert.True(vm.AxisTuningGroups.Single(g => g.Text.EndsWith(": same curve", StringComparison.Ordinal)).IsCurrent);

        // A restart: the preferences file read again.
        string file = Path.Combine(setup.Folder, "list-preferences.json");
        vm.ListPrefs.Save(file);
        MainWindowViewModel again = await setup.OpenAsync(Collective, ListPreferences.Load(file));
        Assert.Equal(three, again.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.Equal("12", again.AxisTuningEditors.Single(e => e.Key == "deadzone").Text);
        // DCS has no curve of its own in this test: the two use the one for all games.
        Assert.Equal(
            [Names(again, "SA342 Copilot", "SA342 Pilot") + ": uses All games", Names(again, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3") + ": same curve"],
            again.AxisTuningGroups.Select(g => g.Text).ToArray());
        Assert.Equal("same as " + Names(again, "AH-64D_BLK_II_PLT", "Ka-50_3"), Row(again, Plane("AH-64D_BLK_II_CPG")).UsesText);

        // A click on a group ticks it: the two that use DCS's curve, i.e. DCS's curve.
        again.TickAxisTuningGroupCommand.Execute(again.AxisTuningGroups[0]);
        await SettleAsync(again);
        Assert.Equal(["game:DCS", Plane("SA342 Copilot"), Plane("SA342 Pilot")], again.AxisTuningTicked.Select(t => t.Scope).ToArray());
        Assert.True(again.AxisTuningGroups[0].IsCurrent);
        // DCS's curve is the one for all games here (dead zone 0): that is what is shown now.
        Assert.Equal("0", again.AxisTuningEditors.Single(e => e.Key == "deadzone").Text);
    }

    [Fact]
    public async Task CopyToAnotherAxis_GoesToTheSameAircraft()
    {
        await using Setup? setup = await CreateAsync();
        if (setup == null)
            return;
        MainWindowViewModel vm = await setup.OpenAsync();
        Row(vm, Plane("SA342 Copilot")).IsChecked = false;
        Row(vm, Plane("SA342 Pilot")).IsChecked = false;
        await SettleAsync(vm);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "9";
        vm.AxisTuningEditors.Single(e => e.Key == "saturationY").Text = "80";
        vm.PrepareAxisTuningCopyCommand.Execute(null);
        vm.AxisTuningCopyTarget = vm.AxisTuningCopyTargets.Single(t => t.Relation == Pedals);
        Assert.Contains(Names(vm, "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3"), vm.AxisTuningCopyText);
        await Task.Delay(2500);
        int before = setup.Versions;
        await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
        await SameMeaningRealEngineTests.WaitLongAsync(() => !vm.IsBusy);
        Assert.Equal(before + 1, setup.Versions);
        Dictionary<string, string> pedals = await setup.ScopesAsync(Pedals);
        foreach (string p in new[] { "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3" })
        {
            AxisTuning copied = AxisTuning.Parse(pedals[Plane(p)]);
            Assert.Equal(0.09, copied.Deadzone, 6);
            Assert.Equal(0.8, copied.SaturationY, 6);
        }
        Assert.False(pedals.ContainsKey(Plane("SA342 Pilot")));
        Assert.False(pedals.ContainsKey("game:DCS"));
        // The source relation is unchanged (nothing saved there).
        Assert.DoesNotContain((await setup.ScopesAsync(Collective)).Keys, k => k.StartsWith("plane:", StringComparison.Ordinal));
    }
}
