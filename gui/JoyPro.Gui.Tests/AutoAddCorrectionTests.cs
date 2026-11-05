using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.6.2: the repair of profiles an earlier load correction filled up (engine profile auto-added: the notice with the
// list to untick, Take them out as one engine run, Keep them; "Default" and the other input layers are no aircraft: not
// counted, last in the list, no notice when there is nothing else), the check banner's words (the button, the aircraft by
// name and what the command does in each; the buttons say the outcome) and its drop rule (the engine's Drop only, never
// an imported aircraft), and a node's title when its aircraft call the id differently.
[SupportedOSPlatform("windows")]
public sealed class AutoAddCorrectionTests
{
    private const string Helo = "THROTTLE - SLIDER -HELO POWER BOTH";

    private static readonly string[] HeloAdded =
        ["a-10a", "A-10C II", "A-10C", "AV8BNA", "Default", "F-15C", "F-4E-45MC", "F-5E-3", "FA-18C_hornet", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33"];

    [Fact]
    public async Task Repair_NoticeOffersExactlyTheAddedAircraftAndTakesThemOutInOneRun()
    {
        AutoAddFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.CheckAutoAddRepairAsync();

        Assert.True(vm.HasAutoAddRepairNotice);
        string day = DateTime.Parse("2026-11-03T13:26:39.0794492Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        // "Default" is no aircraft: the title and the button count the 18 real ones.
        Assert.Equal("JoyPro switched on 18 aircraft you did not choose, in 2 relations, on " + day + ".", vm.AutoAddRepairTitle);
        Assert.StartsWith("When the command lists changed, an older version of JoyPro added every aircraft", vm.AutoAddRepairText);
        Assert.Contains("your own binds stay", vm.AutoAddRepairText);
        Assert.DoesNotContain("not bound by you", vm.AutoAddRepairText);
        Assert.Contains("For example, '" + Helo + "' stays on ", vm.AutoAddRepairText);
        Assert.DoesNotContain("Default,", vm.AutoAddRepairText[..vm.AutoAddRepairText.IndexOf("The entries marked", StringComparison.Ordinal)]);
        Assert.Contains("The entries marked 'not an aircraft' at the end of the list", vm.AutoAddRepairText);
        Assert.Equal(19, vm.AutoAddRepairItems.Count);
        Assert.Equal(17, vm.AutoAddRepairItems.Count(i => i.Text.StartsWith("'" + Helo + "'", StringComparison.Ordinal)));
        Assert.All(vm.AutoAddRepairItems, i => Assert.True(i.IsTicked));
        Assert.Equal("Show the list (18 aircraft in 2 relations)", vm.AutoAddRepairListToggleText);
        // The layer's row comes last and says what it is; a command named like the relation is not repeated.
        Assert.Equal("'" + Helo + "': Default (not an aircraft), command 'Thrust'", vm.AutoAddRepairItems[^1].Text);
        Assert.Equal(1, vm.AutoAddRepairItems.Count(i => i.Text.Contains("not an aircraft", StringComparison.Ordinal)));
        Assert.Contains(vm.AutoAddRepairItems, i => i.Text == "'Pitch': " + vm.AircraftWords("DCS", ["A-10C"]));
        Assert.Contains(vm.AutoAddRepairItems, i => i.Text == "'" + Helo + "': " + vm.AircraftWords("DCS", ["F-15C"]) + ", command 'Thrust'");

        // Untick one: it is skipped; everything else goes in one engine run.
        DuplicateChangeItem keep = vm.AutoAddRepairItems.Single(i => i.Key.EndsWith("\tpitcha10c", StringComparison.Ordinal));
        keep.IsTicked = false;
        fake.Calls.Clear();
        await vm.TakeOutAutoAddedCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "auto-added", "--apply"));
        Assert.True(AxisTuningTests.Seq(apply, "--skip", "pitcha10c"));
        Assert.False(vm.HasAutoAddRepairNotice);
        Assert.Equal("Took out 17 aircraft JoyPro had added. Ctrl+Z undoes it.", vm.StatusText);
    }

    // A tester's 17 "F1 Cockpit view": Default rows and no aircraft: no notice at all, and nothing is changed for it.
    [Fact]
    public async Task Repair_OnlyInputLayersGiveNoNotice()
    {
        AutoAddFakeEngine fake = new() { OnlyLayers = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        fake.Calls.Clear();
        await vm.CheckAutoAddRepairAsync();
        Assert.False(vm.HasAutoAddRepairNotice);
        Assert.Empty(vm.AutoAddRepairItems);
        Assert.Equal(string.Empty, vm.AutoAddRepairText);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "auto-added"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply") || c.Contains("--keep"));
    }

    // One relation left: the singular, in the title and on the button. Without a date the title still reads.
    [Fact]
    public async Task Repair_OneRelationReadsInTheSingular()
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(new AutoAddFakeEngine());
        vm.ApplyAutoAddRepair("{\"Found\":true,\"Steps\":[],\"Relations\":[{\"Relation\":\"Pitch\",\"KeepsAircraft\":[]}]," +
                              "\"Entries\":[{\"Key\":\"k\",\"Relation\":\"Pitch\",\"Game\":\"DCS\",\"Plane\":\"A-10C\",\"Title\":\"- PITCH -\",\"NonAircraft\":false}]}");
        Assert.Equal("JoyPro switched on 1 aircraft you did not choose, in 1 relation.", vm.AutoAddRepairTitle);
        Assert.Equal("Show the list (1 aircraft in 1 relation)", vm.AutoAddRepairListToggleText);
        Assert.DoesNotContain("For example", vm.AutoAddRepairText);
        Assert.DoesNotContain("not an aircraft", vm.AutoAddRepairText);
        Assert.Equal("'Pitch': " + vm.AircraftWords("DCS", ["A-10C"]), Assert.Single(vm.AutoAddRepairItems).Text);
    }

    // The example takes a relation a person named, and an input layer reads as a name, not as an id.
    [Fact]
    public async Task Repair_ExampleAndLayerRowsReadAsWords()
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(new AutoAddFakeEngine());
        const string raw = "Sound_On_Off__Keyboard__S__d122pnilunilcdnilvdnilvpnilvunil__button";
        static string Row(string key, string relation, string plane, bool layer) =>
            "{\"Key\":\"" + key + "\",\"Relation\":\"" + relation + "\",\"Game\":\"DCS\",\"Plane\":\"" + plane + "\",\"Title\":\"\",\"NonAircraft\":" + (layer ? "true" : "false") + "}";
        vm.ApplyAutoAddRepair("{\"Found\":true,\"Steps\":[]," +
                              "\"Relations\":[{\"Relation\":\"" + raw + "\",\"KeepsAircraft\":[\"SA342M\"]},{\"Relation\":\"Pitch\",\"KeepsAircraft\":[\"F-16C_50\"]}]," +
                              "\"Entries\":[" + Row("a", raw, "A-10C", false) + "," + Row("b", raw, "F-15C", false) + "," + Row("c", "Pitch", "A-10C", false) + "," + Row("d", "Pitch", "ground_unit", true) + "]}");
        Assert.Contains("For example, 'Pitch' stays on ", vm.AutoAddRepairText);
        Assert.DoesNotContain(raw, vm.AutoAddRepairText);
        Assert.Equal("'Pitch': Ground Unit (not an aircraft)", vm.AutoAddRepairItems[^1].Text);
    }

    // Unticking every aircraft leaves the input layers: the status names them, not "0 aircraft".
    [Fact]
    public async Task Repair_TakingOutOnlyTheLayersSaysSo()
    {
        AutoAddFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.CheckAutoAddRepairAsync();
        foreach (DuplicateChangeItem item in vm.AutoAddRepairItems.Where(i => !i.Text.Contains("not an aircraft", StringComparison.Ordinal)))
            item.IsTicked = false;
        await vm.TakeOutAutoAddedCommand.ExecuteAsync(null);
        Assert.Equal("Took out the input layers JoyPro had added. Ctrl+Z undoes it.", vm.StatusText);
    }

    [Fact]
    public async Task Repair_KeepThemStopsAskingAndNothingIsTakenOut()
    {
        AutoAddFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.CheckAutoAddRepairAsync();
        Assert.True(vm.HasAutoAddRepairNotice);
        fake.Calls.Clear();
        await vm.KeepAutoAddedCommand.ExecuteAsync(null);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "auto-added", "--keep"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply"));
        Assert.False(vm.HasAutoAddRepairNotice);

        // Nothing found: no notice.
        vm.ApplyAutoAddRepair("{\"Found\":false,\"Entries\":[]}");
        Assert.False(vm.HasAutoAddRepairNotice);
    }

    // "BTN 5 does different things in these aircraft: ... Where should BTN 5 work?" with the outcome on the buttons.
    [Fact]
    public async Task Banner_NamesTheButtonTheAircraftAndTheOutcome()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "two-meanings" };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await vm.RefreshAircraftCheckAsync();
        Assert.True(vm.HasAircraftCheckNotice);
        string hornet = vm.AircraftWords("DCS", ["FA-18C_hornet"]);
        string viper = vm.AircraftWords("DCS", ["F-16C_50"]);
        Assert.Equal("BTN 5 does different things in these aircraft: " + hornet + ": RADAR Switch - EMERG · " + viper + ": KY-58 MODE Knob - RV. Where should BTN 5 work?",
            vm.RelationCheckText);
        Assert.Equal("Only in the " + hornet + " (take it off the " + viper + ")", vm.RelationCheckOnlyLabel);
        Assert.Equal("Keep it in both (" + viper + " keeps KY-58 MODE Knob - RV)", vm.RelationCheckKeepLabel);

        // "Keep it in both" changes nothing in the profile (no engine run), it only stops asking on this PC.
        fake.Calls.Clear();
        vm.KeepRelationCheckForAllCommand.Execute(null);
        Assert.Empty(fake.Calls);
        Assert.Contains("Flare|DCS|d3014pnil|F-16C_50", vm.ListPrefs.KeptAircraft);
        Assert.Contains("does not ask about it again on this PC", vm.StatusText);
    }

    // The engine never proposes an imported aircraft (Ka-50 III's throttle against the Apache's power levers): no banner.
    [Fact]
    public async Task Banner_NeverProposesAnImportedAircraft()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "imported" };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await vm.RefreshAircraftCheckAsync();
        Assert.False(vm.HasAircraftCheckNotice);
        Assert.Empty(vm.AircraftCheckItems);
    }

    // The main meaning is the title; another function and the other names of the same function are lines under it.
    [Fact]
    public async Task NodeTitle_ShowsTheMainMeaningAndTheOthersOnLinesOfTheirOwn()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "none", MixedNode = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.HasAircraft));
        RelationNodeItem node = vm.RelationNodes.Single(n => n.Id == "d3014pnil");
        Assert.Equal("Countermeasures Management Switch - Fwd", node.TitlesSummary);
        string hornet = vm.AircraftWords("DCS", ["FA-18C_hornet"]);
        Assert.Equal(hornet + ": RADAR Switch - EMERG (different function)" + Environment.NewLine + "also called: CMS Forward (2)", node.MeaningNote);
        Assert.True(node.HasMeaningNote);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AutoAddFakeEngine : IEngineCliClient
{
    private readonly BuilderFakeEngine inner = new();

    public List<IReadOnlyList<string>> Calls { get; } = [];

    // check-aircraft: "two-meanings" (Flare's id is the F/A-18C's radar switch and the F-16C's KY-58 knob), "imported"
    // (the other meaning came from the user's files: no Drop), "none".
    public string CheckMode { get; set; } = "none";

    // relations aircraft: Flare's node with four ticked aircraft and three titles.
    public bool MixedNode { get; set; }

    // profile auto-added: nothing but "Default" rows, as in a tester's profile.
    public bool OnlyLayers { get; set; }

    private bool repaired;

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        string? output = Output(arguments);
        if (output == null)
            return inner.RunAsync(engineExePath, arguments, cancellationToken);
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = string.Empty,
            Arguments = arguments.ToArray()
        });
    }

    private static string Entry(string key, string relation, string id, string plane, string title) =>
        "{\"Key\":\"" + key + "\",\"Version\":117,\"Relation\":\"" + relation + "\",\"Game\":\"DCS\",\"Id\":\"" + id + "\",\"Plane\":\"" + plane + "\",\"Title\":\"" + title + "\",\"NonAircraft\":" + (plane == "Default" ? "true" : "false") + ",\"Skipped\":false}";

    private string? Output(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "profile", "auto-added"))
        {
            if (args.Contains("--apply"))
            {
                repaired = true;
                int skipped = args.Contains("--skip") ? args[args.ToList().IndexOf("--skip") + 1].Split(';').Length : 0;
                return "{\"Found\":true,\"Applied\":true,\"Removed\":" + (19 - skipped) + ",\"RemovedNonAircraft\":1,\"Message\":\"\"}";
            }
            if (args.Contains("--keep"))
            {
                repaired = true;
                return "{\"Found\":true,\"Kept\":true,\"Removed\":0,\"Message\":\"\"}";
            }
            if (repaired)
                return "{\"Found\":false,\"Entries\":[]}";
            if (OnlyLayers)
            {
                return "{\"Found\":true,\"Aircraft\":0,\"NonAircraft\":2,\"RelationCount\":0," +
                       "\"Steps\":[{\"Version\":39,\"Utc\":\"2026-10-28T10:00:00.0000000Z\",\"Local\":\"2026-10-28 11:00:00\",\"Label\":\"Deleting relation\",\"Aircraft\":0,\"NonAircraft\":2,\"Relations\":0}]," +
                       "\"Relations\":[{\"Relation\":\"F1 Cockpit view\",\"Remove\":[\"Default\"],\"Keeps\":[\"F-16C_50\"],\"KeepsAircraft\":[\"F-16C_50\"],\"NonAircraft\":[\"Default\"]}," +
                       "{\"Relation\":\"F10 Theater map view\",\"Remove\":[\"Default\"],\"Keeps\":[\"F-16C_50\"],\"KeepsAircraft\":[\"F-16C_50\"],\"NonAircraft\":[\"Default\"]}]," +
                       "\"Entries\":[" + Entry("f1", "F1 Cockpit view", "d7001pnil", "Default", "F1 Cockpit view") + "," + Entry("f10", "F10 Theater map view", "d7010pnil", "Default", "F10 Theater map view") + "],\"Warnings\":[]}";
            }
            List<string> entries = [];
            int n = 0;
            foreach (string plane in new[] { "a-10a", "A-10C II", "A-10C", "AV8BNA", "Default", "F-15C", "F-4E-45MC", "F-5E-3", "FA-18C_hornet", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" })
                entries.Add(Entry("helo" + (n++).ToString(CultureInfo.InvariantCulture), "THROTTLE - SLIDER -HELO POWER BOTH", "a2004cdnil", plane, plane.StartsWith("A-10C", StringComparison.Ordinal) ? "Throttle Both" : "Thrust"));
            entries.Add(Entry("pitchfa18", "Pitch", "a2001cdnil", "FA-18C_hornet", "Pitch"));
            entries.Add(Entry("pitcha10c", "Pitch", "a2001cdnil", "A-10C", "Pitch"));
            return "{\"Found\":true,\"Aircraft\":18,\"NonAircraft\":1,\"RelationCount\":2,\"Applied\":false,\"Kept\":false,\"Removed\":0," +
                   "\"Steps\":[{\"Version\":117,\"Utc\":\"2026-11-03T13:26:39.0794492Z\",\"Local\":\"2026-11-03 14:26:39\",\"Label\":\"Keeping the action in one relation\",\"Aircraft\":18,\"NonAircraft\":1,\"Relations\":2}]," +
                   "\"Relations\":[{\"Relation\":\"Pitch\",\"Remove\":[\"FA-18C_hornet\",\"A-10C\"],\"Keeps\":[\"F-16C_50\"],\"KeepsAircraft\":[\"F-16C_50\"],\"NonAircraft\":[]}," +
                   "{\"Relation\":\"THROTTLE - SLIDER -HELO POWER BOTH\",\"Remove\":[" + string.Join(",", entries.Take(17).Select((_, i) => "\"p" + i + "\"")) + "],\"Keeps\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\",\"Ka-50 III\",\"MosquitoFBMkVI\"],\"KeepsAircraft\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\",\"Ka-50 III\",\"MosquitoFBMkVI\"],\"NonAircraft\":[\"Default\"]}]," +
                   "\"Entries\":[" + string.Join(",", entries) + "],\"Warnings\":[]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "check-aircraft"))
        {
            string found = CheckMode switch
            {
                "two-meanings" => "{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"RADAR Switch - EMERG\",\"Category\":\"\",\"Active\":2,\"Available\":2," +
                                  "\"Meanings\":[{\"Title\":\"KY-58 MODE Knob - RV\",\"Category\":\"\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"F-16C_50\"]}]," +
                                  "\"Drop\":[\"F-16C_50\"],\"SameFunction\":[],\"Imported\":[],\"Planes\":[\"FA-18C_hornet\"]}",
                "imported" => "{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"Power Levers (Both)\",\"Category\":\"\",\"Active\":3,\"Available\":3," +
                              "\"Meanings\":[{\"Title\":\"Throttle\",\"Category\":\"\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"Ka-50 III\"]}]," +
                              "\"Drop\":[],\"SameFunction\":[],\"Imported\":[\"Ka-50 III\"],\"Planes\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\"]}",
                _ => string.Empty
            };
            return "{\"Relations\":1,\"Nodes\":1,\"Found\":[" + found + "]}";
        }
        if (MixedNode && AxisTuningTests.Seq(args, "relations", "aircraft") && !args.Contains("set"))
        {
            return "{\"Relation\":\"Flare\",\"Nodes\":[{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\"," +
                   "\"Active\":4,\"Available\":4,\"Differs\":3,\"DiffersSameFunction\":2,\"Excluded\":0,\"Mixed\":true,\"Badge\":\"4 of 4 aircraft\",\"Aircraft\":[" +
                   "{\"Plane\":\"F-16C_50\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\",\"State\":\"on\",\"Match\":\"same\",\"Note\":\"\"}," +
                   "{\"Plane\":\"FA-18C_hornet\",\"Title\":\"RADAR Switch - EMERG\",\"Category\":\"Sensor\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\"}," +
                   "{\"Plane\":\"A-10C\",\"Title\":\"CMS Forward\",\"Category\":\"HOTAS\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\",\"SameFunction\":true}," +
                   "{\"Plane\":\"AV8BNA\",\"Title\":\"CMS Forward\",\"Category\":\"HOTAS\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\",\"SameFunction\":true}]}]}";
        }
        return null;
    }
}
